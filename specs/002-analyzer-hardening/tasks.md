# Tasks: Analyzer hardening to `latest-recommended`

**Input:** [plan.md](plan.md), [spec.md](spec.md)
**Prerequisites:** feature `001` shared build config in place (`Directory.Build.props`,
`Directory.Packages.props`, `.editorconfig`).

## Format: `[ID] [P?] Description`
- **[P]** = independent file, may run in parallel.

## Phase 1: Documented suppressions (policy)

- [ ] T001 [P] Add `<NoWarn>$(NoWarn);CA1707</NoWarn>` to
  `tests/OracleBi.UniversalPrint.Tests/OracleBi.UniversalPrint.Tests.csproj` (xUnit naming convention).
- [ ] T002 [P] In `.editorconfig`: set `dotnet_diagnostic.CA1711.severity = none` (domain-accurate
  `Queue` naming) and remove the temporary advisory `category-*` overrides added in feature 001.
- [ ] T003 [P] Suppress CA1822 for `PrintTelemetry.StartActivity` via
  `[SuppressMessage("Performance", "CA1822", Justification = "...")]` (kept as instance telemetry API).

## Phase 2: Small genuine fixes

- [ ] T004 [P] CA1305 — in `DeadLetterMonitorFunction.BuildAdaptiveCard`, format
  `DeliveryAttempts.ToString(CultureInfo.InvariantCulture)`.
- [ ] T005 [P] CA1861 — hoist inline constant array arguments to `private static readonly` fields in
  the affected test files (`SubmitProcessorTests`, and any others CA1861 flags).

## Phase 3: LoggerMessage conversion — Core (CA1848 + CA1873)

Each task: make the class `partial`, add `[LoggerMessage]` partial methods copying the existing
message text + level verbatim, and replace the call sites. Build the Core project after each.

- [ ] T006 [P] `Resilience/ResiliencePipelines.cs` (static partial methods taking `ILogger`).
- [ ] T007 [P] `OracleBiIntegration/OracleBiClient.cs`.
- [ ] T008 [P] `UniversalPrintIntegration/UniversalPrintProvider.cs`.
- [ ] T009 [P] `Queueing/AzureStorageDeadLetterQueue.cs` (preserve the `DEAD-LETTER print job` text).
- [ ] T010 [P] `Queueing/AzureStoragePrintJobQueue.cs`.
- [ ] T011 [P] `Polling/PollProcessor.cs`.
- [ ] T012 [P] `Polling/SubmitProcessor.cs`.
- [ ] T013 [P] `Polling/PrintJobService.cs`.
- [ ] T014 [P] `Polling/PrintJobPollingWorker.cs`.

## Phase 4: LoggerMessage conversion — Functions (CA1848 + CA1873)

- [ ] T015 [P] `SubmitPrintJobFunction.cs`.
- [ ] T016 [P] `RenderAndSubmitFunction.cs`.
- [ ] T017 [P] `PollPrintJobFunction.cs`.
- [ ] T018 [P] `DeadLetterMonitorFunction.cs` (alongside the CA1305 fix from T004).

## Phase 5: Switch & verify

- [ ] T019 Flip `Directory.Build.props`: `AnalysisMode=Default` → `Recommended`
  (`latest-recommended`).
- [ ] T020 `dotnet build -c Release` → 0 warnings, 0 errors (SC-001).
- [ ] T021 `dotnet test` → all tests pass, same count (SC-002).

## Dependencies
- Phases 1–2 are independent and can land first.
- Phase 3/4 conversions are independent per file ([P]).
- Phase 5 (T019) must come **after** all conversions/fixes; T020–T021 verify it.

## Acceptance
- Warning-clean `latest-recommended` build; all tests green; log templates/levels unchanged; every
  suppression scoped + documented.
