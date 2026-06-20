namespace OracleBi.UniversalPrint.Models;

/// <summary>
/// A request to render an Oracle BI Publisher report and print it through Universal Print.
/// </summary>
public sealed class OracleBiReportRequest
{
    /// <summary>Absolute report path in the BI Publisher catalog, e.g. /Finance/Invoices/Invoice.xdo.</summary>
    public required string ReportPath { get; init; }

    /// <summary>Output format requested from BI Publisher. PDF is recommended for Universal Print.</summary>
    public string OutputFormat { get; init; } = "pdf";

    /// <summary>Report parameters (name/value) passed to BI Publisher.</summary>
    public IReadOnlyDictionary<string, string> Parameters { get; init; } =
        new Dictionary<string, string>();

    /// <summary>Target Universal Print printer / share id. Falls back to the configured default when null.</summary>
    public string? PrinterId { get; init; }

    /// <summary>Friendly document name shown in the Universal Print queue.</summary>
    public string DocumentName { get; init; } = "Oracle BI Report";
}
