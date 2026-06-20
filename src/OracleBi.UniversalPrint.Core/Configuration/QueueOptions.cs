using System.ComponentModel.DataAnnotations;

namespace OracleBi.UniversalPrint.Configuration;

/// <summary>
/// Azure Storage Queue settings for the poll-scheduling queue and its dead-letter queue.
/// </summary>
public sealed class QueueOptions
{
    public const string SectionName = "Queues";

    /// <summary>
    /// Storage connection string. Leave null and set <see cref="QueueServiceUri"/> to use
    /// managed identity instead.
    /// </summary>
    public string? ConnectionString { get; set; }

    /// <summary>Queue service endpoint, e.g. https://acct.queue.core.windows.net. Used with managed identity.</summary>
    public string? QueueServiceUri { get; set; }

    /// <summary>
    /// Blob service endpoint, e.g. https://acct.blob.core.windows.net. Used with managed identity
    /// for the submit-idempotency container. Falls back to <see cref="ConnectionString"/> when null.
    /// </summary>
    public string? BlobServiceUri { get; set; }

    /// <summary>Name of the queue that holds "poll this job" messages.</summary>
    [Required]
    public string PollQueueName { get; set; } = "print-poll";

    /// <summary>Name of the queue that holds "render + submit this report" messages.</summary>
    [Required]
    public string SubmitQueueName { get; set; } = "print-submit";

    /// <summary>Name of the dead-letter queue for poison / permanently failed messages.</summary>
    [Required]
    public string DeadLetterQueueName { get; set; } = "print-poll-deadletter";

    /// <summary>
    /// Blob container used to record submit idempotency claims (one blob per idempotency key),
    /// so a redelivered submit message cannot create a duplicate Universal Print job.
    /// </summary>
    [Required]
    public string IdempotencyContainerName { get; set; } = "idempotency";

    /// <summary>
    /// Number of delivery attempts allowed before a poll message is moved to the dead-letter
    /// queue. Mirrors the dequeue-count threshold an Azure Functions queue trigger would use.
    /// </summary>
    [Range(1, 100)]
    public int MaxDeliveryAttempts { get; set; } = 5;

    /// <summary>Visibility timeout applied while a poll message is being processed.</summary>
    public TimeSpan VisibilityTimeout { get; set; } = TimeSpan.FromSeconds(30);
}
