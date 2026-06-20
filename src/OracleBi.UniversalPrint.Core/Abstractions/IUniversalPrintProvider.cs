using OracleBi.UniversalPrint.Models;

namespace OracleBi.UniversalPrint.Abstractions;

/// <summary>
/// The custom Universal Print provider for Oracle BI. Submits rendered documents to a
/// Universal Print printer via Microsoft Graph and reports back job status.
/// </summary>
public interface IUniversalPrintProvider
{
    /// <summary>
    /// Renders the Oracle BI report and submits it to Universal Print, returning a tracked
    /// <see cref="PrintJob"/> whose <see cref="PrintJob.UniversalPrintJobId"/> can be polled.
    /// </summary>
    Task<PrintJob> SubmitAsync(
        OracleBiReportRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>Submits an already-rendered document to Universal Print.</summary>
    Task<PrintJob> SubmitDocumentAsync(
        OracleBiReportRequest request,
        OracleBiDocument document,
        CancellationToken cancellationToken = default);

    /// <summary>Fetches the current status of a Universal Print job.</summary>
    Task<PrintJobStatus> GetStatusAsync(
        string printerId,
        string universalPrintJobId,
        CancellationToken cancellationToken = default);
}
