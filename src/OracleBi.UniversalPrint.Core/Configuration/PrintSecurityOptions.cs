namespace OracleBi.UniversalPrint.Configuration;

/// <summary>
/// Authorization guard-rails for what a caller may print. Defence in depth on top of the
/// endpoint's authentication: even an authenticated caller can only render report paths the
/// operator has explicitly allow-listed.
/// </summary>
public sealed class PrintSecurityOptions
{
    public const string SectionName = "PrintSecurity";

    /// <summary>
    /// Report-path prefixes a caller is permitted to print (case-insensitive, matched against the
    /// normalised <c>/</c>-prefixed path). When empty, all paths are allowed — set this in any
    /// shared/multi-tenant environment to prevent arbitrary report exfiltration.
    /// </summary>
    public string[] AllowedReportPathPrefixes { get; set; } = Array.Empty<string>();

    /// <summary>True when no allow-list is configured (all report paths permitted).</summary>
    public bool AllowsAllPaths => AllowedReportPathPrefixes.Length == 0;

    /// <summary>Returns true if <paramref name="reportPath"/> is permitted by the allow-list.</summary>
    public bool IsReportPathAllowed(string reportPath)
    {
        if (AllowsAllPaths)
        {
            return true;
        }

        var normalized = '/' + reportPath.Trim().TrimStart('/');
        return AllowedReportPathPrefixes.Any(prefix =>
            normalized.StartsWith('/' + prefix.Trim().TrimStart('/'), StringComparison.OrdinalIgnoreCase));
    }
}
