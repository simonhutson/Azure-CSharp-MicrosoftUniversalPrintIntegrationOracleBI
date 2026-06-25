using System.Text.Json;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using OracleBi.UniversalPrint.Abstractions;
using OracleBi.UniversalPrint.Models;
using OracleBi.UniversalPrint.Polling;

namespace OracleBi.UniversalPrint.Functions;

/// <summary>
/// Azure Functions queue trigger that renders an Oracle BI report and submits it to Universal
/// Print, off the HTTP request thread. It hosts the shared <see cref="SubmitProcessor"/> — the
/// same idempotency / retry / dead-letter rules regardless of host.
///
/// Flow per message:
///   - Submitted / Duplicate → return (the runtime deletes the message).
///   - DeadLetter            → write a <see cref="DeadLetterEnvelope"/> to the dead-letter queue, then return.
///   - Transient             → throw, so the Functions runtime retries; after host.json maxDequeueCount it
///                             is moved to the poison queue (belt-and-braces alongside our explicit DLQ).
/// </summary>
public sealed partial class RenderAndSubmitFunction
{
    private readonly SubmitProcessor _processor;
    private readonly IDeadLetterQueue _deadLetterQueue;
    private readonly ILogger<RenderAndSubmitFunction> _logger;

    public RenderAndSubmitFunction(
        SubmitProcessor processor,
        IDeadLetterQueue deadLetterQueue,
        ILogger<RenderAndSubmitFunction> logger)
    {
        _processor = processor;
        _deadLetterQueue = deadLetterQueue;
        _logger = logger;
    }

    [Function(nameof(RenderAndSubmitFunction))]
    public async Task RunAsync(
        [QueueTrigger("%Queues:SubmitQueueName%", Connection = "QueueStorage")] string messageText,
        FunctionContext context,
        CancellationToken cancellationToken)
    {
        var dequeueCount = ReadDequeueCount(context);

        SubmitMessage body;
        try
        {
            body = JsonSerializer.Deserialize<SubmitMessage>(messageText)
                   ?? throw new JsonException("Submit message deserialized to null.");
        }
        catch (JsonException ex)
        {
            // Poison message: do not throw (that would just retry the same bad payload). Capture
            // it in the dead-letter queue with full context for inspection / replay.
            LogDeserializeFailed(ex);
            await _deadLetterQueue.DeadLetterAsync(new DeadLetterEnvelope
            {
                CorrelationId = "unknown",
                Reason = DeadLetterReason.DeserializationFailure,
                DeliveryAttempts = (int)dequeueCount,
                ExceptionType = ex.GetType().Name,
                ErrorDetail = messageText,
            }, cancellationToken);
            return;
        }

        var result = await _processor.ProcessAsync(body, dequeueCount, cancellationToken);

        switch (result.Action)
        {
            case SubmitAction.Submitted:
            case SubmitAction.Duplicate:
                // Nothing to do — returning deletes the trigger message.
                break;

            case SubmitAction.DeadLetter:
                await _deadLetterQueue.DeadLetterAsync(result.DeadLetter!, cancellationToken);
                break;
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Failed to deserialize submit message; dead-lettering.")]
    private partial void LogDeserializeFailed(Exception ex);

    private static long ReadDequeueCount(FunctionContext context)
    {
        if (context.BindingContext.BindingData.TryGetValue("DequeueCount", out var raw) &&
            raw is not null &&
            long.TryParse(raw.ToString(), out var count))
        {
            return count;
        }

        return 1;
    }
}
