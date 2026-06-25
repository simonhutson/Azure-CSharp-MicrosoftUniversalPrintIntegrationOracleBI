# Contract 03 — Configuration options

**Tasks:** T007. Create strongly-typed options under
`src/OracleBi.UniversalPrint.Core/Configuration/` in namespace `OracleBi.UniversalPrint.Configuration`.
Each class exposes `public const string SectionName` and uses
`System.ComponentModel.DataAnnotations` for validation. They are bound + validated in DI
(contract 09) with `ValidateDataAnnotations().ValidateOnStart()`.

## Classes

### `OracleBiOptions` — section `"OracleBi"`
- `[Required][Url] string BaseUrl` — BI Publisher base, e.g. `https://obi.contoso.com/xmlpserver`
- `[Required] string Username`
- `[Required] string Password` — resolve from Key Vault in prod, never appsettings in source
- `TimeSpan RequestTimeout = TimeSpan.FromSeconds(100)`
- `bool AllowInsecureTransport` (default false) — allows non-HTTPS base URL for local testing only

### `UniversalPrintOptions` — section `"UniversalPrint"`
- `[Required] string TenantId`
- `[Required] string ClientId`
- `string? ClientSecret` — prefer managed identity/cert in prod
- `bool UseManagedIdentity` — when true, use `DefaultAzureCredential`
- `string GraphBaseUrl = "https://graph.microsoft.com/v1.0"`
- `[Required] string DefaultPrinterId` — printer or printer-share id

### `QueueOptions` — section `"Queues"`
- `string? ConnectionString` — null + `QueueServiceUri` set ⇒ managed identity
- `string? QueueServiceUri` — e.g. `https://acct.queue.core.windows.net`
- `string? BlobServiceUri` — for the idempotency container (managed identity)
- `[Required] string PollQueueName = "print-poll"`
- `[Required] string SubmitQueueName = "print-submit"`
- `[Required] string DeadLetterQueueName = "print-poll-deadletter"`
- `[Required] string IdempotencyContainerName = "idempotency"`
- `[Range(1,100)] int MaxDeliveryAttempts = 5`
- `TimeSpan VisibilityTimeout = TimeSpan.FromSeconds(30)`

### `PollingOptions` — section `"Polling"`
- `TimeSpan DequeueInterval = TimeSpan.FromSeconds(5)`
- `[Range(1,32)] int MaxDegreeOfParallelism = 4`
- `TimeSpan InitialRepollDelay = TimeSpan.FromSeconds(10)`
- `TimeSpan MaxRepollDelay = TimeSpan.FromMinutes(5)`
- `[Range(1,1000)] int MaxPollAttempts = 60`

### `PrintSecurityOptions` — section `"PrintSecurity"`
Report-path allow-list (defence in depth on top of endpoint auth).
- `string[] AllowedReportPathPrefixes = Array.Empty<string>()`
- `bool AllowsAllPaths => AllowedReportPathPrefixes.Length == 0`
- `bool IsReportPathAllowed(string reportPath)`:
  - return true when `AllowsAllPaths`
  - normalise to a single leading `/`: `'/' + reportPath.Trim().TrimStart('/')`
  - return true if any prefix (also normalised to a leading `/`) is a case-insensitive
    `StartsWith` of the normalised path.

## Acceptance

- Project compiles. `PrintSecurityOptions.IsReportPathAllowed` is the only behavioural method here
  and is unit-tested in contract 11.
