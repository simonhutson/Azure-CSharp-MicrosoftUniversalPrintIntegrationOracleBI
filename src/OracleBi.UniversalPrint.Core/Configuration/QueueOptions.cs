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

    /// <summary>Name of the queue that holds "poll this job" messages.</summary>
    [Required]
    public string PollQueueName { get; set; } = "print-poll";

    /// <summary>Name of the dead-letter queue for poison / permanently failed messages.</summary>
    [Required]
    public string DeadLetterQueueName { get; set; } = "print-poll-deadletter";

    /// <summary>
    /// Number of delivery attempts allowed before a poll message is moved to the dead-letter
    /// queue. Mirrors the dequeue-count threshold an Azure Functions queue trigger would use.
    /// </summary>
    [Range(1, 100)]
    public int MaxDeliveryAttempts { get; set; } = 5;

    /// <summary>Visibility timeout applied while a poll message is being processed.</summary>
    public TimeSpan VisibilityTimeout { get; set; } = TimeSpan.FromSeconds(30);
}
