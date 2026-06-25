# Contract 07 — Queue, dead-letter queue & blob idempotency

**Tasks:** T013–T016. Create the Azure Storage Queue integration (poll + submit queues), the
dead-letter queue writer, and the blob-based idempotency store. All support **both** a connection
string (local) and managed identity (`QueueServiceUri` / `BlobServiceUri`). Messages are
Base64-encoded so they are compatible with the Azure Functions queue trigger.

## Abstractions (`Abstractions/`)

### `IPrintJobQueue`
```csharp
public interface IPrintJobQueue
{
    Task EnqueueSubmitAsync(SubmitMessage message, CancellationToken ct = default);
    Task EnqueuePollAsync(PollMessage message, TimeSpan delay, CancellationToken ct = default);
    Task<IReadOnlyList<QueuedPollMessage>> ReceiveAsync(int maxMessages, CancellationToken ct = default);
    Task CompleteAsync(QueuedPollMessage message, CancellationToken ct = default);
    Task AbandonAsync(QueuedPollMessage message, TimeSpan delay, CancellationToken ct = default);
}
```
`QueuedPollMessage` (model or in this namespace): `PollMessage Body`, `string MessageId`,
`string PopReceipt`, `long DequeueCount`, `string? RawBody`.

### `IDeadLetterQueue`
```csharp
public interface IDeadLetterQueue
{
    Task DeadLetterAsync(DeadLetterEnvelope envelope, CancellationToken ct = default);
}
```

### `IIdempotencyStore`
```csharp
public interface IIdempotencyStore
{
    Task<bool> TryClaimAsync(string idempotencyKey, CancellationToken ct = default);
    Task CommitAsync(string idempotencyKey, IdempotencyRecord record, CancellationToken ct = default);
    Task<IdempotencyRecord?> GetAsync(string idempotencyKey, CancellationToken ct = default);
    Task ReleaseAsync(string idempotencyKey, CancellationToken ct = default);
}
```

## `Queueing/AzureStoragePrintJobQueue.cs`
`sealed class AzureStoragePrintJobQueue : IPrintJobQueue`, singleton. Inject `IOptions<QueueOptions>`,
`ILogger`. In the constructor create + `CreateIfNotExists()` a `QueueClient` for the poll queue and
the submit queue via an internal `QueueClientFactory`, and build `CreateOperationPipeline(logger)`.

- `QueueClientFactory.Create(options, queueName)`: `QueueClientOptions { MessageEncoding = Base64 }`;
  use `ConnectionString` if present, else `QueueServiceUri` + `DefaultAzureCredential`, else throw.
- `EnqueueSubmitAsync` / `EnqueuePollAsync`: JSON-serialize, `SendMessageAsync` with `timeToLive = 7 days`;
  for polls pass `visibilityTimeout = delay` (null when ≤ 0). Wrap sends in the resilience pipeline.
- `ReceiveAsync`: `ReceiveMessagesAsync(maxMessages clamped 1..32, VisibilityTimeout)`. For each
  message try to deserialize `PollMessage`; on `JsonException` leave body null and use a
  `PoisonPlaceholder()` (`CorrelationId/PrinterId/UniversalPrintJobId = "unknown"`). Capture
  `MessageId`, `PopReceipt`, `DequeueCount`, `RawBody`.
- `CompleteAsync`: `DeleteMessageAsync(MessageId, PopReceipt)`.
- `AbandonAsync`: `UpdateMessageAsync(MessageId, PopReceipt, RawBody, visibilityTimeout: delay)` —
  re-show after back-off; dequeue count keeps climbing so poison protection still fires.

## `Queueing/AzureStorageDeadLetterQueue.cs`
`sealed class AzureStorageDeadLetterQueue : IDeadLetterQueue`, singleton. Inject `IOptions<QueueOptions>`,
`PrintTelemetry`, `ILogger`. Create + `CreateIfNotExists()` the DLQ `QueueClient`.
`DeadLetterAsync`:
- Serialize the envelope to JSON, `SendMessageAsync` (7-day TTL) through the resilience pipeline.
- Emit telemetry `DeadLettered(envelope.Reason, envelope.PrinterId)`.
- Emit a structured `LogError` `"DEAD-LETTER print job {CorrelationId} reason {Reason} printer {PrinterId} ..."`
  so each field becomes a queryable `customDimension` (this log message text — `"DEAD-LETTER print job"`
  — is what KQL alerts match on). Do **not** include sensitive response bodies.

## `Idempotency/BlobIdempotencyStore.cs`
`sealed class BlobIdempotencyStore : IIdempotencyStore`, singleton. Inject `IOptions<QueueOptions>`,
`ILogger`. Build the container via an internal `BlobContainerClientFactory` (connection string or
`BlobServiceUri` + `DefaultAzureCredential`) using `IdempotencyContainerName`; `CreateIfNotExists()`.
Build `CreateOperationPipeline(logger)`.

- `BlobName(key)` = lowercase hex of `SHA256(key)` (so arbitrary client keys are always valid blob names).
- `TryClaimAsync`: `UploadAsync(empty, BlobUploadOptions { Conditions = { IfNoneMatch = ETag.All } })`
  (atomic "create only if absent"); add metadata `claimedAt`. Catch `RequestFailedException` with
  `Status == 409` → return false (already claimed). Otherwise true. Run inside the pipeline but treat
  409 as a definitive duplicate, not a retryable error.
- `CommitAsync`: overwrite the blob unconditionally with the JSON `IdempotencyRecord` (the durable
  "the Universal Print job exists" marker).
- `GetAsync`: download; empty content ⇒ `new IdempotencyRecord()` (claimed-not-committed); 404 ⇒ null;
  else deserialize.
- `ReleaseAsync`: `DeleteIfExistsAsync` (best-effort).

### Idempotency contract (critical)
First delivery wins the claim. A **committed** claim is **never released**, so a redelivery re-drives
polling but cannot create a duplicate print. A pre-commit failure releases the claim so the queue can
retry. An empty/uncommitted claim left by a crash is bounded by the submit queue visibility timeout
and ultimately dead-letters on max delivery.

## Acceptance

- Project compiles. Behaviour is exercised indirectly by the submit processor tests (contract 11).
