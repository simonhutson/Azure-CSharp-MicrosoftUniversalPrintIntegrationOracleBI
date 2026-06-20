using OracleBi.UniversalPrint.Models;

namespace OracleBi.UniversalPrint.Polling;

/// <summary>What the host should do with a submit message after the processor has evaluated it.</summary>
public enum SubmitAction
{
    /// <summary>Report was rendered and submitted to Universal Print; delete the message.</summary>
    Submitted,

    /// <summary>The idempotency key was already claimed; this is a duplicate. Delete the message.</summary>
    Duplicate,

    /// <summary>Submit failed permanently; write <see cref="SubmitProcessingResult.DeadLetter"/> and delete the message.</summary>
    DeadLetter,
}

/// <summary>The decision returned by <see cref="SubmitProcessor"/> for a single submit message.</summary>
public sealed class SubmitProcessingResult
{
    public required SubmitAction Action { get; init; }

    /// <summary>The tracked job, set when <see cref="Action"/> is <see cref="SubmitAction.Submitted"/>.</summary>
    public PrintJob? Job { get; init; }

    /// <summary>Set when <see cref="Action"/> is <see cref="SubmitAction.DeadLetter"/>.</summary>
    public DeadLetterEnvelope? DeadLetter { get; init; }

    public static SubmitProcessingResult Submitted(PrintJob job) =>
        new() { Action = SubmitAction.Submitted, Job = job };

    public static SubmitProcessingResult Duplicate() =>
        new() { Action = SubmitAction.Duplicate };

    public static SubmitProcessingResult Dead(DeadLetterEnvelope envelope) =>
        new() { Action = SubmitAction.DeadLetter, DeadLetter = envelope };
}
