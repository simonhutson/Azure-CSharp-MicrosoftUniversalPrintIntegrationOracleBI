using System.Text.Json;
using Azure.Storage.Queues;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OracleBi.UniversalPrint.Abstractions;
using OracleBi.UniversalPrint.Configuration;
using OracleBi.UniversalPrint.Models;
using OracleBi.UniversalPrint.Resilience;
using OracleBi.UniversalPrint.Telemetry;
using Polly;

namespace OracleBi.UniversalPrint.Queueing;

/// <summary>
/// Azure Storage Queue dead-letter queue. Writes a <see cref="DeadLetterEnvelope"/> as JSON and
/// emits a structured log + metric so the event can be alerted on and correlated to its job.
/// </summary>
public sealed class AzureStorageDeadLetterQueue : IDeadLetterQueue
{
    private readonly QueueClient _queue;
    private readonly ILogger<AzureStorageDeadLetterQueue> _logger;
    private readonly PrintTelemetry _telemetry;
    private readonly ResiliencePipeline _pipeline;

    public AzureStorageDeadLetterQueue(
        IOptions<QueueOptions> options,
        ILogger<AzureStorageDeadLetterQueue> logger,
        PrintTelemetry telemetry)
    {
        _queue = QueueClientFactory.Create(options.Value, options.Value.DeadLetterQueueName);
        _queue.CreateIfNotExists();
        _logger = logger;
        _telemetry = telemetry;
        _pipeline = ResiliencePipelines.CreateOperationPipeline(logger);
    }

    public async Task DeadLetterAsync(DeadLetterEnvelope envelope, CancellationToken cancellationToken = default)
    {
        var json = JsonSerializer.Serialize(envelope);

        await _pipeline.ExecuteAsync(
            async ct => await _queue.SendMessageAsync(json, cancellationToken: ct),
            cancellationToken);

        // Metric: drives the DLQ dashboard tiles and metric alerts.
        _telemetry.DeadLettered(envelope.Reason, envelope.PrinterId);

        // Structured log: every field becomes a queryable customDimension in Application Insights,
        // which is what lets you correlate a DLQ event back to the originating print job.
        _logger.LogError(
            "DEAD-LETTER print job. CorrelationId={CorrelationId} Reason={Reason} PrinterId={PrinterId} " +
            "UniversalPrintJobId={UniversalPrintJobId} DeliveryAttempts={DeliveryAttempts} " +
            "ExceptionType={ExceptionType} Detail={Detail}",
            envelope.CorrelationId,
            envelope.ReasonCode,
            envelope.PrinterId,
            envelope.UniversalPrintJobId,
            envelope.DeliveryAttempts,
            envelope.ExceptionType,
            envelope.ErrorDetail);
    }
}
