using System.Text.Json;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using OracleBi.UniversalPrint.Abstractions;
using OracleBi.UniversalPrint.Models;
using OracleBi.UniversalPrint.Polling;

namespace OracleBi.UniversalPrint.Functions;

/// <summary>
/// Azure Functions queue trigger that polls Universal Print job status. It is an alternative
/// host for the shared <see cref="PollProcessor"/> — the same retry / dead-letter rules used by
/// the in-process background worker.
///
/// Flow per message:
///   - Completed  → return (the runtime deletes the message).
///   - Reschedule → enqueue a new poll message with a visibility delay, then return.
///   - DeadLetter → write a <see cref="DeadLetterEnvelope"/> to the dead-letter queue, then return.
///   - Transient  → throw, so the Functions runtime retries; after host.json maxDequeueCount it
///                  is moved to the poison queue (belt-and-braces alongside our explicit DLQ).
/// </summary>
public sealed class PollPrintJobFunction
{
    private readonly PollProcessor _processor;
    private readonly IPrintJobQueue _queue;
    private readonly IDeadLetterQueue _deadLetterQueue;
    private readonly ILogger<PollPrintJobFunction> _logger;

    public PollPrintJobFunction(
        PollProcessor processor,
        IPrintJobQueue queue,
        IDeadLetterQueue deadLetterQueue,
        ILogger<PollPrintJobFunction> logger)
    {
        _processor = processor;
        _queue = queue;
        _deadLetterQueue = deadLetterQueue;
        _logger = logger;
    }

    [Function(nameof(PollPrintJobFunction))]
    public async Task RunAsync(
        // Raw message text; the queue name + connection are resolved from configuration.
        [QueueTrigger("%Queues:PollQueueName%", Connection = "QueueStorage")] string messageText,
        FunctionContext context,
        CancellationToken cancellationToken)
    {
        // DequeueCount drives our poison protection. In the isolated worker it arrives via the
        // trigger binding metadata rather than a strongly-typed message object.
        var dequeueCount = ReadDequeueCount(context);

        PollMessage body;
        try
        {
            body = JsonSerializer.Deserialize<PollMessage>(messageText)
                   ?? throw new JsonException("Poll message deserialized to null.");
        }
        catch (JsonException ex)
        {
            // Poison message: do not throw (that would just retry the same bad payload). Capture
            // it in the dead-letter queue with full context for inspection / replay.
            _logger.LogError(ex, "Failed to deserialize poll message; dead-lettering.");
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
            case PollAction.Completed:
                // Nothing to do — returning deletes the trigger message.
                break;

            case PollAction.Reschedule:
                await _queue.EnqueuePollAsync(result.NextMessage!, result.Delay, cancellationToken);
                break;

            case PollAction.DeadLetter:
                await _deadLetterQueue.DeadLetterAsync(result.DeadLetter!, cancellationToken);
                break;
        }
    }

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
