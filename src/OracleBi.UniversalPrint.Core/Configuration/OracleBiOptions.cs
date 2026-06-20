using System.ComponentModel.DataAnnotations;

namespace OracleBi.UniversalPrint.Configuration;

/// <summary>
/// Connection settings for the Oracle BI Publisher REST API that produces the
/// report output (PDF) we want to print.
/// </summary>
public sealed class OracleBiOptions
{
    public const string SectionName = "OracleBi";

    /// <summary>Base URL of the BI Publisher server, e.g. https://obi.contoso.com/xmlpserver.</summary>
    [Required]
    [Url]
    public string BaseUrl { get; set; } = string.Empty;

    /// <summary>Service account user used to authenticate to BI Publisher.</summary>
    [Required]
    public string Username { get; set; } = string.Empty;

    /// <summary>
    /// Service account password. In production resolve this from Key Vault / a secret store,
    /// never from appsettings committed to source control.
    /// </summary>
    [Required]
    public string Password { get; set; } = string.Empty;

    /// <summary>Per-request HTTP timeout for Oracle BI calls.</summary>
    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(100);

    /// <summary>
    /// Allows a non-HTTPS <see cref="BaseUrl"/>. Defaults to false so Basic auth credentials are
    /// never sent over plaintext. Enable only for local testing against an http test server.
    /// </summary>
    public bool AllowInsecureTransport { get; set; }
}
