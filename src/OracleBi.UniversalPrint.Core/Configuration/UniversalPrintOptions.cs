using System.ComponentModel.DataAnnotations;

namespace OracleBi.UniversalPrint.Configuration;

/// <summary>
/// Settings for talking to Microsoft Universal Print via Microsoft Graph.
/// The app registration needs the application permissions
/// <c>PrintJob.ReadWrite.All</c> and <c>Printer.Read.All</c> (admin consented).
/// </summary>
public sealed class UniversalPrintOptions
{
    public const string SectionName = "UniversalPrint";

    /// <summary>Entra ID tenant id.</summary>
    [Required]
    public string TenantId { get; set; } = string.Empty;

    /// <summary>App registration (client) id.</summary>
    [Required]
    public string ClientId { get; set; } = string.Empty;

    /// <summary>
    /// Client secret. Prefer a managed identity / certificate in production; this is here to
    /// keep the sample self-contained.
    /// </summary>
    public string? ClientSecret { get; set; }

    /// <summary>
    /// When true, use <see cref="Azure.Identity.DefaultAzureCredential"/> (managed identity in
    /// Azure, developer credentials locally) instead of a client secret.
    /// </summary>
    public bool UseManagedIdentity { get; set; }

    /// <summary>Graph base address. Universal Print job APIs currently live under v1.0 and beta.</summary>
    public string GraphBaseUrl { get; set; } = "https://graph.microsoft.com/v1.0";

    /// <summary>The default Universal Print printer (or printer share) id to submit jobs to.</summary>
    [Required]
    public string DefaultPrinterId { get; set; } = string.Empty;
}
