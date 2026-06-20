using OracleBi.UniversalPrint.Models;

namespace OracleBi.UniversalPrint.Abstractions;

/// <summary>
/// Abstraction over the poll-scheduling queue. Implemented on Azure Storage Queues but kept
/// behind an interface so the polling logic can be unit tested without Azure.
/// </summary>
public interface IPrintJobQueue
{
    /// <summary>Schedules a job to be polled after <paramref name="delay"/>.</summary>
    Task EnqueuePollAsync(PollMessage message, TimeSpan delay, CancellationToken cancellationToken = default);

    /// <summary>Receives a batch of due poll messages (already-visible) for processing.</summary>
    Task<IReadOnlyList<QueuedPollMessage>> ReceiveAsync(
        int maxMessages,
        CancellationToken cancellationToken = default);

    /// <summary>Deletes a successfully processed message from the queue.</summary>
    Task CompleteAsync(QueuedPollMessage message, CancellationToken cancellationToken = default);

    /// <summary>
    /// Makes a message visible again after <paramref name="delay"/> so it is retried later
    /// (used for transient failures and for re-polling non-terminal jobs).
    /// </summary>
    Task AbandonAsync(QueuedPollMessage message, TimeSpan delay, CancellationToken cancellationToken = default);
}

/// <summary>A poll message received from the queue, including its delivery metadata.</summary>
public sealed class QueuedPollMessage
{
    public required PollMessage Body { get; init; }

    /// <summary>Underlying queue message id.</summary>
    public required string MessageId { get; init; }

    /// <summary>Pop receipt required to delete / update the message.</summary>
    public required string PopReceipt { get; init; }

    /// <summary>How many times this message has been dequeued (delivery attempt counter).</summary>
    public required long DequeueCount { get; init; }

    /// <summary>Raw message text, retained so a poison message can be forwarded verbatim to the DLQ.</summary>
    public string? RawBody { get; init; }
}
