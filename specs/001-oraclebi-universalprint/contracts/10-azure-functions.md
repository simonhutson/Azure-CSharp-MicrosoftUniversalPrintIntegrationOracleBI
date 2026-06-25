# Contract 10 — Azure Functions host & functions

**Tasks:** T027–T032. Create the .NET isolated-worker Functions app under
`src/OracleBi.UniversalPrint.Functions/` (namespace `OracleBi.UniversalPrint.Functions`). It hosts the
shared processors via four functions plus a single OpenTelemetry pipeline exported to Azure Monitor.

## `host.json`
```json
{
  "version": "2.0",
  "telemetryMode": "OpenTelemetry",
  "logging": {
    "applicationInsights": { "samplingSettings": { "isEnabled": true, "excludedTypes": "Request" } },
    "logLevel": { "default": "Information", "OracleBi.UniversalPrint": "Information" }
  },
  "extensions": {
    "queues": {
      "maxPollingInterval": "00:00:10",
      "visibilityTimeout": "00:00:30",
      "batchSize": 16,
      "maxDequeueCount": 5,
      "newBatchThreshold": 8
    }
  }
}
```

## `Program.cs`
`HostBuilder().ConfigureFunctionsWorkerDefaults().ConfigureServices(...)`:
- `services.AddOracleBiUniversalPrint(context.Configuration);`
- One OpenTelemetry pipeline (do **not** add the classic App Insights SDK — avoid double export):
  ```csharp
  services.AddOpenTelemetry()
      .UseFunctionsWorkerDefaults()
      .WithTracing(t => t.AddSource(PrintTelemetry.ActivitySourceName).AddHttpClientInstrumentation())
      .WithMetrics(m => m.AddMeter(PrintTelemetry.MeterName))
      .UseAzureMonitorExporter();   // reads APPLICATIONINSIGHTS_CONNECTION_STRING
  ```
- `host.Run();`

## Functions

### `SubmitPrintJobFunction` (HTTP)
- `[HttpTrigger(AuthorizationLevel.Function, "post", Route = "print-jobs")]`.
- Read `OracleBiReportRequest` from JSON; on parse failure or missing `ReportPath` → `400`.
- Enforce the allow-list (`PrintSecurityOptions.IsReportPathAllowed`); disallowed → `403` (reject
  synchronously rather than accepting then failing on the queue).
- Generate `correlationId = Guid.NewGuid().ToString("N")`; read optional `Idempotency-Key` header
  (else generate one).
- `EnqueueSubmitAsync(new SubmitMessage { CorrelationId, IdempotencyKey, Request })`.
- Return `202 Accepted` with JSON `{ correlationId, idempotencyKey, state = "Accepted" }`.

### `RenderAndSubmitFunction` (queue trigger — submit queue)
- `[QueueTrigger("%Queues:SubmitQueueName%", Connection = "QueueStorage")] string messageText`, plus
  `FunctionContext`. Read `DequeueCount` from `context.BindingContext.BindingData`.
- Deserialize `SubmitMessage`; on `JsonException` → write a `DeserializationFailure` DLQ envelope and
  return (do **not** throw — that would just retry the poison payload).
- `result = SubmitProcessor.ProcessAsync(body, dequeueCount)`. Apply: `Submitted`/`Duplicate` →
  return (deletes the message); `DeadLetter` → `DeadLetterAsync`.

### `PollPrintJobFunction` (queue trigger — poll queue)
- `[QueueTrigger("%Queues:PollQueueName%", Connection = "QueueStorage")] string messageText` + context.
- Same dequeue/deserialize/poison handling as above but for `PollMessage`.
- `result = PollProcessor.ProcessAsync(body, dequeueCount)`. Apply: `Completed` → return;
  `Reschedule` → `EnqueuePollAsync(next, delay)`; `DeadLetter` → `DeadLetterAsync`.
- For transient failures the processor **throws** → the runtime retries; after `maxDequeueCount` the
  message goes to `<queue>-poison` (belt-and-braces alongside the explicit DLQ).

### `DeadLetterMonitorFunction` (queue trigger — DLQ)
- `[QueueTrigger("%Queues:DeadLetterQueueName%", Connection = "QueueStorage")] string messageText`.
- Deserialize `DeadLetterEnvelope`; on failure log + return.
- `LogCritical` an ALERT line with `CorrelationId`, `ReasonCode`, `PrinterId`.
- Read `Notifications:WebhookUrl`; if empty, log + return (rely on Azure Monitor alerts).
- Else POST an **Adaptive Card** (`application/vnd.microsoft.card.adaptive`, version 1.4) with a
  FactSet of CorrelationId / Reason / Printer / UP Job Id / Delivery attempts / When. **Omit the raw
  error detail** (may be sensitive) — direct the reader to App Insights via the correlation id.
- If the webhook returns non-success, **throw** so the runtime retries (the alert must be reliable).

## `local.settings.json` (local dev against Azurite)
Keys: `AzureWebJobsStorage` + `QueueStorage` = `UseDevelopmentStorage=true`;
`FUNCTIONS_WORKER_RUNTIME = dotnet-isolated`; `APPLICATIONINSIGHTS_CONNECTION_STRING`;
`OracleBi:*` (BaseUrl https, Username, Password, `AllowInsecureTransport=false`);
`UniversalPrint:*` (TenantId/ClientId/ClientSecret/`UseManagedIdentity=false`/DefaultPrinterId);
`Queues:*` (ConnectionString=`UseDevelopmentStorage=true`, PollQueueName=`print-poll`,
SubmitQueueName=`print-submit`, DeadLetterQueueName=`print-poll-deadletter`,
IdempotencyContainerName=`idempotency`, `MaxDeliveryAttempts=5`);
`Polling:*` (InitialRepollDelay `00:00:10`, MaxRepollDelay `00:05:00`, `MaxPollAttempts=60`);
`PrintSecurity:AllowedReportPathPrefixes:0` = `/Finance/`; `Notifications:WebhookUrl` = "".

> Note: `QueueStorage` is the connection name used by every queue trigger binding. In Azure it is
> set as `QueueStorage__queueServiceUri` for identity-based access (contract 12).

## Acceptance

- `dotnet build` succeeds. `func start` (with Azurite running) starts the host and lists the four
  functions; `POST /api/print-jobs` returns `202` with a `correlationId`.
