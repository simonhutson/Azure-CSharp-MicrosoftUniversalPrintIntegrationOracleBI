using OracleBi.UniversalPrint.Models;

namespace OracleBi.UniversalPrint.Abstractions;

/// <summary>
/// Renders a report from Oracle BI Publisher and returns its bytes.
/// </summary>
public interface IOracleBiClient
{
    /// <summary>
    /// Calls BI Publisher to render <paramref name="request"/> and returns the document bytes
    /// together with the resolved content type (e.g. application/pdf).
    /// </summary>
    Task<OracleBiDocument> RenderReportAsync(
        OracleBiReportRequest request,
        CancellationToken cancellationToken = default);
}

/// <summary>Rendered report payload returned by Oracle BI Publisher.</summary>
public sealed class OracleBiDocument
{
    public required byte[] Content { get; init; }

    public required string ContentType { get; init; }

    public required string FileName { get; init; }

    public long Length => Content.LongLength;
}
