using System.Text.Json;
using Azure;
using Azure.Identity;
using Azure.Storage.Queues;
using Azure.Storage.Queues.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OracleBi.UniversalPrint.Abstractions;
using OracleBi.UniversalPrint.Configuration;
using OracleBi.UniversalPrint.Models;
using OracleBi.UniversalPrint.Resilience;
using Polly;

namespace OracleBi.UniversalPrint.Queueing;

/// <summary>
/// Azure Storage Queue implementation of the poll-scheduling queue. Poll messages are JSON,
/// Base64 encoded (so they are also readable by an Azure Functions queue trigger).
/// </summary>
public sealed class AzureStoragePrintJobQueue : IPrintJobQueue
{
    private readonly QueueClient _queue;
    private readonly QueueClient _submitQueue;
    private readonly QueueOptions _options;
    private readonly ResiliencePipeline _pipeline;

    public AzureStoragePrintJobQueue(IOptions<QueueOptions> options, ILogger<AzureStoragePrintJobQueue> logger)
    {
        _options = options.Value;
        _queue = QueueClientFactory.Create(_options, _options.PollQueueName);
        _queue.CreateIfNotExists();
        _submitQueue = QueueClientFactory.Create(_options, _options.SubmitQueueName);
        _submitQueue.CreateIfNotExists();
        _pipeline = ResiliencePipelines.CreateOperationPipeline(logger);
    }

    public async Task EnqueueSubmitAsync(SubmitMessage message, CancellationToken cancellationToken = default)
    {
        var json = JsonSerializer.Serialize(message);
        await _pipeline.ExecuteAsync(
            async ct => await _submitQueue.SendMessageAsync(
                json,
                timeToLive: TimeSpan.FromDays(7),
                cancellationToken: ct),
            cancellationToken);
    }

    public async Task EnqueuePollAsync(PollMessage message, TimeSpan delay, CancellationToken cancellationToken = default)
    {
        var json = JsonSerializer.Serialize(message);
        await _pipeline.ExecuteAsync(
            async ct => await _queue.SendMessageAsync(
                json,
                visibilityTimeout: delay <= TimeSpan.Zero ? null : delay,
                timeToLive: TimeSpan.FromDays(7),
                cancellationToken: ct),
            cancellationToken);
    }

    public async Task<IReadOnlyList<QueuedPollMessage>> ReceiveAsync(
        int maxMessages, CancellationToken cancellationToken = default)
    {
        var response = await _pipeline.ExecuteAsync(
            async ct => await _queue.ReceiveMessagesAsync(
                maxMessages: Math.Clamp(maxMessages, 1, 32),
                visibilityTimeout: _options.VisibilityTimeout,
                cancellationToken: ct),
            cancellationToken);

        var result = new List<QueuedPollMessage>();
        foreach (var msg in response.Value)
        {
            PollMessage? body = null;
            try
            {
                body = JsonSerializer.Deserialize<PollMessage>(msg.MessageText);
            }
            catch (JsonException)
            {
                // Leave body null; the worker will dead-letter this as a poison message.
            }

            result.Add(new QueuedPollMessage
            {
                Body = body ?? PoisonPlaceholder(),
                MessageId = msg.MessageId,
                PopReceipt = msg.PopReceipt,
                DequeueCount = msg.DequeueCount,
                RawBody = msg.MessageText,
            });
        }

        return result;
    }

    public async Task CompleteAsync(QueuedPollMessage message, CancellationToken cancellationToken = default)
    {
        await _pipeline.ExecuteAsync(
            async ct => await _queue.DeleteMessageAsync(message.MessageId, message.PopReceipt, ct),
            cancellationToken);
    }

    public async Task AbandonAsync(QueuedPollMessage message, TimeSpan delay, CancellationToken cancellationToken = default)
    {
        // Update visibility so the message reappears after the back-off delay. The dequeue count
        // continues to climb so the delivery-attempt threshold still triggers dead-lettering.
        await _pipeline.ExecuteAsync(
            async ct => await _queue.UpdateMessageAsync(
                message.MessageId,
                message.PopReceipt,
                message.RawBody,
                visibilityTimeout: delay,
                cancellationToken: ct),
            cancellationToken);
    }

    private static PollMessage PoisonPlaceholder() => new()
    {
        CorrelationId = "unknown",
        PrinterId = "unknown",
        UniversalPrintJobId = "unknown",
    };
}

/// <summary>Builds <see cref="QueueClient"/> instances using either a connection string or managed identity.</summary>
internal static class QueueClientFactory
{
    public static QueueClient Create(QueueOptions options, string queueName)
    {
        // Base64 encoding keeps messages compatible with the Azure Functions queue trigger.
        var clientOptions = new QueueClientOptions { MessageEncoding = QueueMessageEncoding.Base64 };

        if (!string.IsNullOrWhiteSpace(options.ConnectionString))
        {
            return new QueueClient(options.ConnectionString, queueName, clientOptions);
        }

        if (!string.IsNullOrWhiteSpace(options.QueueServiceUri))
        {
            var uri = new Uri($"{options.QueueServiceUri.TrimEnd('/')}/{queueName}");
            return new QueueClient(uri, new DefaultAzureCredential(), clientOptions);
        }

        throw new InvalidOperationException(
            "QueueOptions requires either ConnectionString or QueueServiceUri to be set.");
    }
}
