using OracleBi.UniversalPrint.Models;

namespace OracleBi.UniversalPrint.UniversalPrintIntegration;

/// <summary>
/// Maps Universal Print's <c>processingState</c> + <c>details</c> codes (from Microsoft Graph)
/// into our normalised <see cref="PrintJobState"/>. Extracted from the provider so the mapping
/// rules can be unit tested directly.
/// </summary>
internal static class UniversalPrintStatusMapper
{
    public static PrintJobState MapState(string? processingState, IReadOnlyList<string> details)
    {
        if (details.Any(d => d.Equals("completedSuccessfully", StringComparison.OrdinalIgnoreCase)))
        {
            return PrintJobState.Completed;
        }

        if (details.Any(d =>
                d.Contains("aborted", StringComparison.OrdinalIgnoreCase) ||
                d.Contains("error", StringComparison.OrdinalIgnoreCase) ||
                d.Contains("interrupted", StringComparison.OrdinalIgnoreCase)))
        {
            return PrintJobState.Failed;
        }

        return processingState?.ToLowerInvariant() switch
        {
            "completed" => PrintJobState.Completed,
            "aborted" or "canceled" or "cancelled" => PrintJobState.Failed,
            "processing" or "pending" or "paused" => PrintJobState.Printing,
            _ => PrintJobState.Printing,
        };
    }
}
