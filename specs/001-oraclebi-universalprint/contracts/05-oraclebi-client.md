# Contract 05 — Oracle BI Publisher client

**Tasks:** T010. Create the abstraction and the REST client that renders a BI Publisher report to bytes.

## Abstraction — `Abstractions/IOracleBiClient.cs`

```csharp
public interface IOracleBiClient
{
    Task<OracleBiDocument> RenderReportAsync(
        OracleBiReportRequest request, CancellationToken cancellationToken = default);
}
```

## Client — `OracleBiIntegration/OracleBiClient.cs`

`sealed class OracleBiClient : IOracleBiClient`, registered as a typed `HttpClient`
(`AddHttpClient<IOracleBiClient, OracleBiClient>()`). Inject `HttpClient`,
`IOptions<OracleBiOptions>`, `ILogger<OracleBiClient>`, `PrintTelemetry`. Build a
`CreateHttpPipeline(logger)` in the constructor.

### Constructor behaviour
- Compute `baseUri = new Uri(options.BaseUrl.TrimEnd('/') + "/")`.
- **HTTPS enforcement**: if `!AllowInsecureTransport` and the scheme is not HTTPS, throw
  `InvalidOperationException("OracleBi:BaseUrl must use HTTPS. Set OracleBi:AllowInsecureTransport=true only for local testing.")`.
- `httpClient.BaseAddress ??= baseUri;` and `httpClient.Timeout = options.RequestTimeout;`.
- Set Basic auth: `Convert.ToBase64String(Encoding.UTF8.GetBytes($"{Username}:{Password}"))` into
  `AuthenticationHeaderValue("Basic", ...)`.

### `RenderReportAsync`
- Start activity `"oraclebi.render"` with `request.ReportPath` as correlation id; tag
  `oraclebi.report_path` and `oraclebi.output_format`.
- BI Publisher REST endpoint: `POST {base}/services/rest/v1/reports/{encodedPath}/run` where
  `encodedPath = Uri.EscapeDataString(request.ReportPath.TrimStart('/'))`.
- JSON payload:
  ```json
  {
    "attributeFormat": "<OutputFormat>",
    "attributeLocale": "en-US",
    "parameterNameValues": {
      "listOfParamNameValues": [
        { "item": { "name": "<k>", "values": { "item": ["<v>"] } } }
      ]
    }
  }
  ```
  Build `listOfParamNameValues` from `request.Parameters`.
- Execute through the resilience pipeline. **Rebuild the `HttpRequestMessage` and its content inside
  the pipeline delegate** so retried POSTs are safe. Use
  `HttpCompletionOption.ResponseHeadersRead`; add `Accept: application/octet-stream`.
- On non-success: `LogError` with **status only** (never the body at Error — it may contain PII);
  optionally log a truncated (≤512 char) body at Debug. Throw `OracleBiRenderException`.
- On success: read bytes, derive content type (default `application/pdf`), build
  `FileName = "{SanitizeFileName(DocumentName)}.{MapExtension(OutputFormat)}"`, tag
  `oraclebi.bytes`, log info, return the `OracleBiDocument`.

### Helpers
- `MapExtension`: pdf→pdf, html→html, rtf→rtf, excel/xlsx→xlsx, else bin (lowercased switch).
- `SanitizeFileName`: replace `Path.GetInvalidFileNameChars()` with `_`.
- `Truncate(value, max)`.

### Exception
`public sealed class OracleBiRenderException(string message) : Exception(message);` (same file).

## Acceptance

- Project compiles. The HTTPS guard throws when `BaseUrl` is http and `AllowInsecureTransport=false`.
