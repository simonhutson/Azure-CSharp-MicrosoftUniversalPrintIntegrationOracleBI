using OracleBi.UniversalPrint.Models;

namespace OracleBi.UniversalPrint.Polling;

/// <summary>What the host should do with a poll message after the processor has evaluated it.</summary>
public enum PollAction
{
    /// <summary>Job reached a terminal success state; delete the message.</summary>
    Completed,

    /// <summary>Job is still printing; delete this message and enqueue <see cref="PollProcessingResult.NextMessage"/>.</summary>
    Reschedule,

    /// <summary>Job failed permanently; write <see cref="PollProcessingResult.DeadLetter"/> and delete the message.</summary>
    DeadLetter,
}

/// <summary>The decision returned by <see cref="PollProcessor"/> for a single poll message.</summary>
public sealed class PollProcessingResult
{
    public required PollAction Action { get; init; }

    /// <summary>Set when <see cref="Action"/> is <see cref="PollAction.Reschedule"/>.</summary>
    public PollMessage? NextMessage { get; init; }

    /// <summary>Back-off delay before the rescheduled poll becomes visible.</summary>
    public TimeSpan Delay { get; init; }

    /// <summary>Set when <see cref="Action"/> is <see cref="PollAction.DeadLetter"/>.</summary>
    public DeadLetterEnvelope? DeadLetter { get; init; }

    public static PollProcessingResult Done() => new() { Action = PollAction.Completed };

    public static PollProcessingResult Later(PollMessage next, TimeSpan delay) =>
        new() { Action = PollAction.Reschedule, NextMessage = next, Delay = delay };

    public static PollProcessingResult Dead(DeadLetterEnvelope envelope) =>
        new() { Action = PollAction.DeadLetter, DeadLetter = envelope };
}
