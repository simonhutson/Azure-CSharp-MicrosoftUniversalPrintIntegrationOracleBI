# Contract 11 — Unit tests

**Tasks:** T023–T026. Create xUnit tests under `tests/OracleBi.UniversalPrint.Tests/`. Target the
pure, host-agnostic logic — no Azure calls. Use hand-written fakes/stubs for
`IUniversalPrintProvider`, `IIdempotencyStore`, etc. (no mocking library is required).
`InternalsVisibleTo` (set in contract 01) exposes the internal status mapper.

## `UniversalPrintStatusMapperTests.cs`
Cover `UniversalPrintStatusMapper.MapState`:
- detail `"completedSuccessfully"` → `Completed`.
- details containing `aborted` / `error` / `interrupted` → `Failed`.
- `processingState` = `processing`/`pending`/`paused` → `Printing`.
- `processingState` = `completed` → `Completed`; `aborted`/`canceled`/`cancelled` → `Failed`.
- unknown/null state → `Printing`.

## `PrintSecurityOptionsTests.cs`
Cover `PrintSecurityOptions.IsReportPathAllowed`:
- empty allow-list → everything allowed (`AllowsAllPaths`).
- prefix `/Finance/` allows `/Finance/Invoices/x` (and the leading-slash-insensitive variant) but
  rejects `/HR/x`.
- case-insensitive matching.

## `PollProcessorTests.cs`
Drive `PollProcessor.ProcessAsync` with a stub `IUniversalPrintProvider` returning a chosen
`PrintJobStatus`, and option values via `Options.Create(...)`:
- status `Completed` → `PollAction.Completed`.
- status `Failed` → `PollAction.DeadLetter` with reason `PrintJobFailed`.
- still printing under the attempt budget → `PollAction.Reschedule` with `PollAttempts` incremented
  and a back-off delay capped at `MaxRepollDelay`.
- still printing at/over `MaxPollAttempts` → `DeadLetter` reason `PollTimeoutExceeded`.
- `deliveryAttempt > MaxDeliveryAttempts` → `DeadLetter` reason `MaxDeliveryAttemptsExceeded`.
- provider throws a transient exception → `ProcessAsync` rethrows (host will abandon).

## `SubmitProcessorTests.cs`
Drive `SubmitProcessor.ProcessAsync` with a fake `IIdempotencyStore` (in-memory dictionary) and a
`PrintJobService` over a stub provider:
- fresh key → claim wins, job submitted, committed, first poll scheduled → `SubmitAction.Submitted`.
- duplicate key already committed → re-drives polling, no second provider call → `Duplicate`.
- duplicate key uncommitted → throws `SubmitClaimPendingException`.
- report path not allowed → `DeadLetter` reason `SubmitRejected`, claim retained.
- pre-submit provider failure → claim released + exception rethrown.
- `deliveryAttempt > MaxDeliveryAttempts` → `DeadLetter` reason `MaxDeliveryAttemptsExceeded`.

## Acceptance

- `dotnet test` passes. Tests use only fakes — no Azurite, no network.
