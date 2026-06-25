# Contract 02 — Domain models & enums

**Tasks:** T006. Create the model types under `src/OracleBi.UniversalPrint.Core/Models/` in namespace
`OracleBi.UniversalPrint.Models`. These are plain DTOs/records used across the provider, queue
messages, idempotency and the DLQ. Keep them `sealed`.

## Types to create

### `PrintJobState.cs` (enum)
States: `Pending`, `Submitting`, `Printing`, `Completed`, `Failed`, `Abandoned`. This is the
normalised internal state (Graph's `processingState`/`details` map into it — see contract 06).

### `OracleBiReportRequest.cs`
The caller's request. Properties:
- `string ReportPath` (required, BI Publisher catalog path, e.g. `/Finance/Invoices/Invoice.xdo`)
- `string OutputFormat` (default `"pdf"`; also html/rtf/excel/xlsx)
- `string DocumentName` (used for the printed document file name)
- `IReadOnlyDictionary<string,string> Parameters` (report parameters; default empty)
- `string? PrinterId` (optional override of the default printer)

### `OracleBiDocument.cs`
Rendered output: `byte[] Content`, `string ContentType`, `string FileName`.

### `PrintJob.cs`
A tracked job. Properties: `string CorrelationId`, `OracleBiReportRequest Request`,
`string? PrinterId`, `string? UniversalPrintJobId`, `PrintJobState State`, `string? LastError`,
`DateTimeOffset CreatedAt = UtcNow`, `DateTimeOffset UpdatedAt`.

### `PrintJobStatus.cs`
Result of a status poll: `PrintJobState State`, `string? RawProcessingState`, `string? Description`,
`IReadOnlyList<string> Details`.

### `SubmitMessage.cs`
Queue message for the async submit path: `string CorrelationId`, `string IdempotencyKey`,
`OracleBiReportRequest Request`.

### `PollMessage.cs`
Queue message for one status poll: `string CorrelationId`, `string PrinterId`,
`string UniversalPrintJobId`, `int PollAttempts`, `DateTimeOffset ScheduledFor`.

### `IdempotencyRecord.cs`
Commit marker stored in the idempotency blob: `string? CorrelationId`, `string? UniversalPrintJobId`,
`string? PrinterId`, `DateTimeOffset? CommittedAt`. Add a computed `bool IsCommitted =>
CommittedAt is not null && UniversalPrintJobId is not null;`. An empty record (default) means
"claimed but not committed".

### `DeadLetterEnvelope.cs` + `DeadLetterReason` (enum)
`DeadLetterReason` values (used for routing, dashboards, alerts):
`MaxDeliveryAttemptsExceeded`, `DeserializationFailure`, `PrintJobFailed`, `SubmitRejected`,
`PollTimeoutExceeded`, `UnhandledError`.

`DeadLetterEnvelope` (written to the DLQ; carries enough to correlate + replay):
- `required string CorrelationId`
- `required DeadLetterReason Reason`
- `string ReasonCode => Reason.ToString()`
- `string? PrinterId`, `string? UniversalPrintJobId`
- `int DeliveryAttempts`
- `string? ExceptionType`, `string? ErrorDetail`
- `PollMessage? OriginalMessage` (the original poll message for replay, when available)
- `DateTimeOffset DeadLetteredAt = UtcNow`

## Acceptance

- Project compiles. No business logic yet — these are data holders only.
