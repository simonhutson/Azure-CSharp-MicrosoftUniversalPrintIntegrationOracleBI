# Contract 06 — Universal Print provider & status mapper

**Tasks:** T011–T012. Create the custom Universal Print provider that talks to the Microsoft Graph
print APIs, plus the status mapper that normalises Graph job status into `PrintJobState`.

## Abstraction — `Abstractions/IUniversalPrintProvider.cs`

```csharp
public interface IUniversalPrintProvider
{
    Task<PrintJob> SubmitAsync(OracleBiReportRequest request, string correlationId, CancellationToken ct = default);
    Task<PrintJob> SubmitDocumentAsync(OracleBiReportRequest request, OracleBiDocument document, string correlationId, CancellationToken ct = default);
    Task<PrintJobStatus> GetStatusAsync(string printerId, string universalPrintJobId, CancellationToken ct = default);
}
```

## Status mapper — `UniversalPrintIntegration/UniversalPrintStatusMapper.cs`

`internal static class UniversalPrintStatusMapper` with
`PrintJobState MapState(string? processingState, IReadOnlyList<string> details)`:
- If any detail equals `"completedSuccessfully"` (case-insensitive) → `Completed`.
- If any detail contains `aborted` / `error` / `interrupted` (case-insensitive) → `Failed`.
- Else switch on lowercased `processingState`: `completed`→Completed; `aborted`/`canceled`/`cancelled`→Failed;
  `processing`/`pending`/`paused`→Printing; default→Printing.

(Internal so it is unit-testable via `InternalsVisibleTo` set in contract 01.)

## Provider — `UniversalPrintIntegration/UniversalPrintProvider.cs`

`sealed class UniversalPrintProvider : IUniversalPrintProvider`, registered as a typed `HttpClient`.
Inject `HttpClient`, `IOracleBiClient`, `IOptions<UniversalPrintOptions>`,
`ILogger<UniversalPrintProvider>`, `PrintTelemetry`. Build `CreateHttpPipeline(logger)`.

### Constants & auth
- `UploadChunkSize = 5 * 320 * 1024` (1.6 MB — Graph upload chunks must be a multiple of 320 KiB).
- `GraphScopes = ["https://graph.microsoft.com/.default"]`.
- Credential: if `UseManagedIdentity` or no `ClientSecret` → `DefaultAzureCredential`; else
  `ClientSecretCredential(TenantId, ClientId, ClientSecret)`.
- `httpClient.BaseAddress ??= new Uri(options.GraphBaseUrl.TrimEnd('/') + "/")`.

### Graph print flow (the heart of the provider)
`SubmitAsync` renders via `IOracleBiClient.RenderReportAsync`, then calls `SubmitDocumentAsync`.

`SubmitDocumentAsync`:
- `printerId = request.PrinterId ?? options.DefaultPrinterId`.
- Start activity `"universalprint.submit"`, tag `printer.id`.
- Create a `PrintJob` (State `Printing`).
- Steps, each via `SendAsync` + `EnsureSuccessAsync`:
  1. **Create job** — `POST print/printers/{printerId}/jobs` with body
     `{ configuration = { feedOrientation = "longEdgeFirst", quality = "medium" } }`; read `id`.
  2. **Get document id** — `GET print/printers/{printerId}/jobs/{jobId}/documents`; take `value[0].id`
     (throw `UniversalPrintException` if empty).
  3. **Create upload session** — `POST .../documents/{documentId}/createUploadSession` with the
     document `properties` (documentName, contentType, size).
  4. **Upload** the bytes in `UploadChunkSize` chunks using `Content-Range` headers to the returned
     `uploadUrl` (PUT each chunk).
  5. **Start job** — `POST print/printers/{printerId}/jobs/{jobId}/start`.
- On success: set `job.UniversalPrintJobId`, `UpdatedAt`, `telemetry.JobSubmitted(printerId)`, log info, return.
- On exception: set State `Failed`, `LastError`, `telemetry.JobFailed(printerId, ex.GetType().Name)`,
  `LogError` (no response body), rethrow.

`GetStatusAsync`:
- `GET print/printers/{printerId}/jobs/{jobId}?$select=id,status`.
- Parse `status.state`, `status.description`, and `status.details[]` (string array).
- Return `PrintJobStatus` with `State = UniversalPrintStatusMapper.MapState(state, details)` plus the
  raw fields.

### Shared helpers
- `SendAsync(HttpMethod, uri, Func<HttpContent>? contentFactory, ct)`: acquires a Graph token via the
  credential (`GetTokenAsync(new TokenRequestContext(GraphScopes))`), sets the bearer header, builds the
  request (rebuilding content from `contentFactory` so retries are safe), executes through the pipeline.
- `EnsureSuccessAsync(response, operation, ct)`: throw `UniversalPrintException` on non-success
  including status + operation, **without** embedding the response body in the message at Error level.
- `UniversalPrintException` — `sealed`, in the same namespace.

## Acceptance

- Project compiles. The status mapper is pure and unit-testable (contract 11).
