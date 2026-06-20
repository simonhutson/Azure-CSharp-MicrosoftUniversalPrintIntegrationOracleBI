namespace OracleBi.UniversalPrint.Models;

/// <summary>
/// Normalised lifecycle state for a print job, abstracting away the differences between
/// Oracle BI rendering and Universal Print processing states.
/// </summary>
public enum PrintJobState
{
    /// <summary>Job accepted, report not yet rendered.</summary>
    Pending,

    /// <summary>Oracle BI is rendering the report output.</summary>
    Rendering,

    /// <summary>Document uploaded to Universal Print and queued / printing.</summary>
    Printing,

    /// <summary>Universal Print reports the job completed successfully.</summary>
    Completed,

    /// <summary>Job failed in a way that may be retried.</summary>
    Failed,

    /// <summary>Job failed permanently and has been dead-lettered.</summary>
    Abandoned,
}

/// <summary>True when the state will not change again without external intervention.</summary>
public static class PrintJobStateExtensions
{
    public static bool IsTerminal(this PrintJobState state) =>
        state is PrintJobState.Completed or PrintJobState.Abandoned;
}
