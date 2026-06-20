using System.ComponentModel.DataAnnotations;

namespace OracleBi.UniversalPrint.Configuration;

/// <summary>
/// Controls the status-polling behaviour for in-flight print jobs.
/// </summary>
public sealed class PollingOptions
{
    public const string SectionName = "Polling";

    /// <summary>How often the worker pulls due poll messages from the queue.</summary>
    public TimeSpan DequeueInterval { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>Number of poll messages processed concurrently.</summary>
    [Range(1, 32)]
    public int MaxDegreeOfParallelism { get; set; } = 4;

    /// <summary>Base delay before the first re-poll of a job that is still printing.</summary>
    public TimeSpan InitialRepollDelay { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>Upper bound for the exponential back-off between re-polls.</summary>
    public TimeSpan MaxRepollDelay { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Maximum number of times a single job will be re-polled before it is treated as a
    /// failure and dead-lettered (prevents jobs stuck "processing" forever).
    /// </summary>
    [Range(1, 1000)]
    public int MaxPollAttempts { get; set; } = 60;
}
