# Contract 08 — Polling & submit processors, worker, PrintJobService

**Tasks:** T017–T021. Create the **host-agnostic** core of the async pipeline. The retry / reschedule
/ dead-letter rules live here exactly once and are shared by the in-process background worker (this
contract) and the Azure Functions queue triggers (contract 10).

All types go under `src/OracleBi.UniversalPrint.Core/Polling/`.

## Result types

### `PollProcessingResult` + `PollAction`
`enum PollAction { Completed, Reschedule, DeadLetter }`. `sealed class PollProcessingResult` with
`PollAction Action`, `PollMessage? NextMessage`, `TimeSpan Delay`, `DeadLetterEnvelope? DeadLetter`,
and static factories `Done()`, `Reschedule(PollMessage next, TimeSpan delay)`, `Dead(DeadLetterEnvelope e)`.

### `SubmitProcessingResult` + `SubmitAction`
`enum SubmitAction { Submitted, Duplicate, DeadLetter }`. `sealed class SubmitProcessingResult` with
`SubmitAction Action`, `PrintJob? Job`, `DeadLetterEnvelope? DeadLetter`, and static factories
`Submitted(PrintJob job)`, `Duplicate()`, `Dead(DeadLetterEnvelope e)`.

## `PrintJobService`
The application entry point used by callers and the submit processor. Inject
`IUniversalPrintProvider`, `IPrintJobQueue`, `IOptions<PollingOptions>`, `IOptions<PrintSecurityOptions>`,
`ILogger`.
- `SubmitAsync(request, correlationId, ct)`: enforce the report-path allow-list (throw
  `ReportPathNotAllowedException` on violation), call `provider.SubmitAsync`, validate the returned job
  has `UniversalPrintJobId` and `PrinterId`, return it. **This is the irreversible step** (creates a
  Graph print job) — callers must guard it with idempotency.
- `ScheduleFirstPollAsync(correlationId, printerId, jobId, ct)`: enqueue a `PollMessage`
  (`PollAttempts = 0`, `ScheduledFor = UtcNow + InitialRepollDelay`) with visibility delay
  `InitialRepollDelay`. Safe to call more than once (a duplicate poll is harmless).
- `SubmitAndTrackAsync(...)`: convenience = `SubmitAsync` then `ScheduleFirstPollAsync`.
- `ReportPathNotAllowedException(string reportPath)` — sealed, in the same file, exposes `ReportPath`.

## `PollProcessor`
`sealed`. Inject `IUniversalPrintProvider`, `IOptions<PollingOptions>`, `IOptions<QueueOptions>`,
`PrintTelemetry`, `ILogger`.
`Task<PollProcessingResult> ProcessAsync(PollMessage message, long deliveryAttempt, CancellationToken ct)`:
- Start activity `"print.poll"`; tag printer/job/poll-attempt/delivery-attempt.
- **Poison protection**: if `deliveryAttempt > QueueOptions.MaxDeliveryAttempts` →
  `Dead(MaxDeliveryAttemptsExceeded)`.
- Call `provider.GetStatusAsync` timed with a `Stopwatch`. Rethrow `OperationCanceledException`;
  for other exceptions `LogWarning` and **rethrow** (the provider already retried transient errors; the
  host abandons the message so its dequeue count climbs toward poison protection).
- `telemetry.PollAttempted(printerId, elapsedMs, status.State)`.
- Switch on `status.State`:
  - `Completed` → `telemetry.JobCompleted(printerId, UtcNow - message.ScheduledFor)`, log, `Done()`.
  - `Failed` or `Abandoned` → `telemetry.JobFailed`, `LogError`, `Dead(PrintJobFailed, detail = description/details)`.
  - default (still printing): `nextAttempt = PollAttempts + 1`. If `>= MaxPollAttempts` →
    `Dead(PollTimeoutExceeded)`. Else compute **exponential back-off** delay from `InitialRepollDelay`
    doubling per attempt, capped at `MaxRepollDelay`; return `Reschedule(nextMessage, delay)` where
    `nextMessage` increments `PollAttempts` and updates `ScheduledFor`.
- Private `BuildEnvelope(message, reason, deliveryAttempt, exceptionType, detail)` populating a
  `DeadLetterEnvelope` (including `OriginalMessage = message`).

## `SubmitProcessor`
`sealed`. Inject `PrintJobService`, `IIdempotencyStore`, `IOptions<QueueOptions>`, `PrintTelemetry`,
`ILogger`.
`Task<SubmitProcessingResult> ProcessAsync(SubmitMessage message, long deliveryAttempt, CancellationToken ct)`:
- Start activity `"print.submit"`.
- Poison protection: `deliveryAttempt > MaxDeliveryAttempts` → `Dead(MaxDeliveryAttemptsExceeded)`.
- `claimed = idempotency.TryClaimAsync(message.IdempotencyKey)`. If not claimed →
  `HandleDuplicateAsync` (below).
- Try `job = printJobService.SubmitAsync(...)`:
  - catch `ReportPathNotAllowedException` → keep the claim, `Dead(SubmitRejected)`.
  - catch other exceptions → **release the claim best-effort** (with `CancellationToken.None`) and
    rethrow so the host retries.
- After success the Graph job exists; **never release the claim or call the provider again**:
  1. `idempotency.CommitAsync(key, new IdempotencyRecord { CorrelationId, UniversalPrintJobId, PrinterId, CommittedAt = UtcNow })`.
  2. `printJobService.ScheduleFirstPollAsync(...)`.
  3. return `Submitted(job)`.
- `HandleDuplicateAsync`: `GetAsync(key)`. If `record.IsCommitted` → re-drive
  `ScheduleFirstPollAsync` (the provider is never called again) and return `Duplicate()`. Else throw
  `SubmitClaimPendingException(correlationId)` so the host retries (recovers once the in-flight attempt
  commits, or dead-letters on max delivery).
- `SubmitClaimPendingException(string correlationId)` — sealed, exposes `CorrelationId`.

## `PrintJobPollingWorker`
`sealed class PrintJobPollingWorker : BackgroundService`. Inject `IPrintJobQueue`, `IDeadLetterQueue`,
`PollProcessor`, `IOptions<PollingOptions>`, `ILogger`. (Optional in-process host — the Functions
trigger is the alternative.)
- Loop until cancelled: `ReceiveAsync(MaxDegreeOfParallelism)`; if empty `Task.Delay(DequeueInterval)`.
- Process the batch with bounded parallelism (`System.Threading.Channels` + N workers =
  `MaxDegreeOfParallelism`). Never let the loop die — catch, log, back off.
- Per message:
  - Poison (body `CorrelationId == "unknown"` with `RawBody`) → DLQ `DeserializationFailure` + `CompleteAsync`.
  - Else `processor.ProcessAsync(body, DequeueCount)` and apply the action: `Completed`→`CompleteAsync`;
    `Reschedule`→`EnqueuePollAsync(next, delay)` then `CompleteAsync`; `DeadLetter`→`DeadLetterAsync` then `CompleteAsync`.
  - On `OperationCanceledException` during shutdown: leave the message for redelivery.
  - On other exception: `AbandonAsync(message, InitialRepollDelay)` (dequeue count climbs → poison protection).

## Acceptance

- Project compiles. `PollProcessor` and `SubmitProcessor` are directly unit-tested in contract 11.
