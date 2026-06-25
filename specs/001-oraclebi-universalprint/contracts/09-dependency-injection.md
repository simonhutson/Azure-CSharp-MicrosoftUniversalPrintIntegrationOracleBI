# Contract 09 — Dependency injection registration

**Tasks:** T022. Create the registration helpers under
`src/OracleBi.UniversalPrint.Core/DependencyInjection/ServiceCollectionExtensions.cs` in namespace
`OracleBi.UniversalPrint.DependencyInjection`. These are called by both the Functions host (contract
10) and any Worker/App Service host.

## `static class ServiceCollectionExtensions`

### `IServiceCollection AddOracleBiUniversalPrint(this IServiceCollection services, IConfiguration configuration)`

Register, in this order:

1. **Options** with binding + validation:
   - `OracleBiOptions` — bind `OracleBiOptions.SectionName`, `ValidateDataAnnotations()`, `ValidateOnStart()`.
   - `UniversalPrintOptions` — bind, validate, `ValidateOnStart()`.
   - `PollingOptions` — bind, `ValidateDataAnnotations()` (no `ValidateOnStart`).
   - `QueueOptions` — bind, validate, `ValidateOnStart()`.
   - `PrintSecurityOptions` — bind only.
2. `services.AddSingleton<PrintTelemetry>();`
3. Typed HTTP clients:
   - `services.AddHttpClient<IOracleBiClient, OracleBiClient>();`
   - `services.AddHttpClient<IUniversalPrintProvider, UniversalPrintProvider>();`
4. Singletons:
   - `IPrintJobQueue` → `AzureStoragePrintJobQueue`
   - `IDeadLetterQueue` → `AzureStorageDeadLetterQueue`
   - `IIdempotencyStore` → `BlobIdempotencyStore`
   - `PollProcessor`, `SubmitProcessor`, `PrintJobService`
5. `return services;`

### `IServiceCollection AddPrintPollingWorker(this IServiceCollection services)`

`services.AddHostedService<PrintJobPollingWorker>(); return services;`

Call this **only** when hosting polling in-process (Worker Service / App Service / container). The
Azure Functions deployment relies on the queue-trigger functions instead and does **not** call it.

## Acceptance

- Project compiles. A host can now do
  `services.AddOracleBiUniversalPrint(configuration)` (optionally `.AddPrintPollingWorker()`).
