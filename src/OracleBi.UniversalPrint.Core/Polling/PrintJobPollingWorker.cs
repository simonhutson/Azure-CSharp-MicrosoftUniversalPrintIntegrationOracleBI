using System.Threading.Channels;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OracleBi.UniversalPrint.Abstractions;
using OracleBi.UniversalPrint.Configuration;
using OracleBi.UniversalPrint.Models;

namespace OracleBi.UniversalPrint.Polling;

/// <summary>
/// Background polling worker. Continuously pulls due poll messages from the Azure Storage Queue,
/// evaluates them with the shared <see cref="PollProcessor"/>, and applies the resulting action
/// (complete / reschedule / dead-letter). Use this when you host polling inside a long-running
/// process (Worker Service, App Service, container). The Azure Functions queue trigger is an
/// alternative host for the same <see cref="PollProcessor"/> logic.
/// </summary>
public sealed class PrintJobPollingWorker : BackgroundService
{
    private readonly IPrintJobQueue _queue;
    private readonly IDeadLetterQueue _deadLetterQueue;
    private readonly PollProcessor _processor;
    private readonly PollingOptions _options;
    private readonly ILogger<PrintJobPollingWorker> _logger;

    public PrintJobPollingWorker(
        IPrintJobQueue queue,
        IDeadLetterQueue deadLetterQueue,
        PollProcessor processor,
        IOptions<PollingOptions> options,
        ILogger<PrintJobPollingWorker> logger)
    {
        _queue = queue;
        _deadLetterQueue = deadLetterQueue;
        _processor = processor;
        _options = options.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "Print job polling worker started (parallelism={Parallelism}, interval={Interval}).",
            _options.MaxDegreeOfParallelism, _options.DequeueInterval);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var messages = await _queue.ReceiveAsync(_options.MaxDegreeOfParallelism, stoppingToken);
                if (messages.Count == 0)
                {
                    await Task.Delay(_options.DequeueInterval, stoppingToken);
                    continue;
                }

                await ProcessBatchAsync(messages, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // Never let the loop die; back off briefly and continue.
                _logger.LogError(ex, "Polling loop iteration failed; backing off.");
                await Task.Delay(_options.DequeueInterval, stoppingToken);
            }
        }

        _logger.LogInformation("Print job polling worker stopped.");
    }

    private async Task ProcessBatchAsync(IReadOnlyList<QueuedPollMessage> messages, CancellationToken ct)
    {
        var channel = Channel.CreateBounded<QueuedPollMessage>(messages.Count);
        foreach (var m in messages)
        {
            await channel.Writer.WriteAsync(m, ct);
        }
        channel.Writer.Complete();

        var workers = Enumerable.Range(0, _options.MaxDegreeOfParallelism)
            .Select(_ => Task.Run(async () =>
            {
                await foreach (var message in channel.Reader.ReadAllAsync(ct))
                {
                    await ProcessOneAsync(message, ct);
                }
            }, ct));

        await Task.WhenAll(workers);
    }

    private async Task ProcessOneAsync(QueuedPollMessage message, CancellationToken ct)
    {
        // Poison message: body could not be deserialized — dead-letter it verbatim.
        if (message.Body.CorrelationId == "unknown" && message.RawBody is not null)
        {
            await _deadLetterQueue.DeadLetterAsync(new DeadLetterEnvelope
            {
                CorrelationId = "unknown",
                Reason = DeadLetterReason.DeserializationFailure,
                DeliveryAttempts = (int)message.DequeueCount,
                ErrorDetail = Truncate(message.RawBody, 1024),
            }, ct);
            await _queue.CompleteAsync(message, ct);
            return;
        }

        try
        {
            var result = await _processor.ProcessAsync(message.Body, message.DequeueCount, ct);

            switch (result.Action)
            {
                case PollAction.Completed:
                    await _queue.CompleteAsync(message, ct);
                    break;

                case PollAction.Reschedule:
                    await _queue.EnqueuePollAsync(result.NextMessage!, result.Delay, ct);
                    await _queue.CompleteAsync(message, ct);
                    break;

                case PollAction.DeadLetter:
                    await _deadLetterQueue.DeadLetterAsync(result.DeadLetter!, ct);
                    await _queue.CompleteAsync(message, ct);
                    break;
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Host is shutting down; leave the message for redelivery.
        }
        catch (Exception ex)
        {
            // Transient/unexpected failure: make the message visible again after a short back-off.
            // Its dequeue count climbs, so poison protection eventually dead-letters it.
            _logger.LogWarning(ex,
                "Processing poll message {CorrelationId} failed; abandoning for retry.",
                message.Body.CorrelationId);
            await _queue.AbandonAsync(message, _options.InitialRepollDelay, ct);
        }
    }

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max];
}
