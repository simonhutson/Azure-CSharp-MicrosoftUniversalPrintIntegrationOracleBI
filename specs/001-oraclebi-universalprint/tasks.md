# Tasks: Oracle BI → Microsoft Universal Print provider

**Input:** Design documents from `specs/001-oraclebi-universalprint/`
**Prerequisites:** [plan.md](plan.md) (required), [spec.md](spec.md) (user stories & requirements)

**Tests:** Unit tests for the pure decision core are REQUIRED by Constitution Principle VII. They are
included in Phase 7 and mapped to the stories they protect.

**Organization:** Tasks are grouped by the build phases in `plan.md`. Each phase ends with a
checkpoint (build/test) that must be green before proceeding.

## Format: `[ID] [P?] [Story] Description`
- **[P]** = may run in parallel (different files, no dependency on another incomplete task)
- **[Story]** = user story the task primarily serves (US1–US4) or `Infra`/`Setup`/`Core`

## Path Conventions
Paths below are repository-relative and match `plan.md`'s structure
(`src/OracleBi.UniversalPrint.Core/`, `src/OracleBi.UniversalPrint.Functions/`,
`tests/OracleBi.UniversalPrint.Tests/`, `infra/`).

---

## Phase 1: Scaffold (Setup) — see [contracts/01-scaffold.md](contracts/01-scaffold.md)

- [ ] T001 [Setup] Create `OracleBi.UniversalPrint.sln` with `src` and `tests` solution folders.
- [ ] T002 [P] [Setup] Create `src/OracleBi.UniversalPrint.Core/OracleBi.UniversalPrint.Core.csproj`
  (net10.0, ImplicitUsings, Nullable, LangVersion latest, `TreatWarningsAsErrors=true`,
  `InternalsVisibleTo` Tests) with the package baseline (Azure.Identity, Azure.Storage.Queues,
  Azure.Storage.Blobs, Polly.Core, Microsoft.Extensions.*, OpenTelemetry, Azure.Monitor exporter).
- [ ] T003 [P] [Setup] Create
  `src/OracleBi.UniversalPrint.Functions/OracleBi.UniversalPrint.Functions.csproj` (net10.0, v4,
  OutputType Exe, Worker + Worker.Sdk + Storage.Queues + Http + Worker.OpenTelemetry + Azure.Monitor
  exporter; ProjectReference to Core; host.json/local.settings.json copy rules).
- [ ] T004 [P] [Setup] Create
  `tests/OracleBi.UniversalPrint.Tests/OracleBi.UniversalPrint.Tests.csproj` (net10.0, xUnit, test SDK,
  ProjectReference to Core).
- [ ] T005 [Setup] Add all three projects to the solution and confirm `dotnet build` restores.

**Checkpoint:** `dotnet build` compiles three empty projects.

---

## Phase 2: Domain models & configuration (Foundational) — see [contracts/02-domain-models.md](contracts/02-domain-models.md), [contracts/03-configuration-options.md](contracts/03-configuration-options.md)

**⚠️ BLOCKS all later phases.**

- [ ] T006 [P] [Core] Create model enums/records in `src/OracleBi.UniversalPrint.Core/Models/`:
  `PrintJobState.cs`, `OracleBiReportRequest.cs`, `OracleBiDocument.cs`, `PrintJob.cs`,
  `PrintJobStatus.cs`, `SubmitMessage.cs`, `PollMessage.cs`, `IdempotencyRecord.cs`,
  `DeadLetterEnvelope.cs` (+ `DeadLetterReason`). Carry `CorrelationId` on job, messages, and envelope
  (Principle I).
- [ ] T007 [P] [Core] Create options in `src/OracleBi.UniversalPrint.Core/Configuration/`:
  `OracleBiOptions.cs`, `UniversalPrintOptions.cs`, `QueueOptions.cs`, `PollingOptions.cs`,
  `PrintSecurityOptions.cs` (DataAnnotations; `PrintSecurityOptions.IsReportPathAllowed` allow-list).

**Checkpoint:** Core compiles; models + options exist (no behaviour beyond the allow-list method).

---

## Phase 3: Cross-cutting telemetry & resilience (Foundational) — see [contracts/04-telemetry-resilience.md](contracts/04-telemetry-resilience.md)

**⚠️ Consumed by every outbound caller; do before integrations.**

- [ ] T008 [P] [Core] Create `src/OracleBi.UniversalPrint.Core/Telemetry/PrintTelemetry.cs` — one
  `ActivitySource` + one `Meter` named `OracleBi.UniversalPrint`, all instruments
  (`print.jobs.submitted/completed/failed`, `print.poll.attempts/latency`, `print.job.duration`,
  `print.deadletter.count`) and `StartActivity` stamping `print.correlation_id` (Principle VI).
- [ ] T009 [P] [Core] Create `src/OracleBi.UniversalPrint.Core/Resilience/ResiliencePipelines.cs` —
  Polly v8 HTTP pipeline (transient-only, jittered back-off, honour `Retry-After`, bounded) and an
  operation pipeline for queue/blob I/O (Principle III).

**Checkpoint:** Core compiles with telemetry + resilience available.

---

## Phase 4: Integrations — see [contracts/05-oraclebi-client.md](contracts/05-oraclebi-client.md), [contracts/06-universalprint-provider.md](contracts/06-universalprint-provider.md)

- [ ] T010 [P] [US2] Create `Abstractions/IOracleBiClient.cs` and
  `OracleBiIntegration/OracleBiClient.cs` — BI Publisher REST render via typed `HttpClient` + HTTP
  pipeline; enforce HTTPS Basic auth; never log response bodies at error; rebuild request per attempt.
- [ ] T011 [P] [US3] Create `UniversalPrintIntegration/UniversalPrintStatusMapper.cs` (internal, pure)
  mapping Graph `processingState`/`details` → `PrintJobState`.
- [ ] T012 [US2] Create `Abstractions/IUniversalPrintProvider.cs` and
  `UniversalPrintIntegration/UniversalPrintProvider.cs` — Graph print flow (create job → get document
  → create upload session → chunked upload at 320 KiB multiples → start) + `GetStatusAsync` using the
  mapper; managed-identity-or-secret credential; tags `printer.id`; telemetry on submit/fail.
  *(Depends on T011.)*

**Checkpoint:** Core compiles; render + submit + status contracts implemented.

---

## Phase 5: Messaging & state — see [contracts/07-queue-idempotency.md](contracts/07-queue-idempotency.md)

- [ ] T013 [P] [Core] Create `Abstractions/IPrintJobQueue.cs`, `Abstractions/IDeadLetterQueue.cs`,
  `Abstractions/IIdempotencyStore.cs` and the `QueuedPollMessage` type.
- [ ] T014 [US1] Create `Queueing/AzureStoragePrintJobQueue.cs` — submit/poll enqueue (Base64,
  7-day TTL, visibility delay), receive/complete/abandon, connection-string-or-managed-identity factory.
  *(Depends on T013.)*
- [ ] T015 [US4] Create `Queueing/AzureStorageDeadLetterQueue.cs` — DLQ write emitting BOTH
  `print.deadletter.count` and a structured `DEAD-LETTER print job` `LogError` (Principle VI).
  *(Depends on T013.)*
- [ ] T016 [US2] Create `Idempotency/BlobIdempotencyStore.cs` — claim (If-None-Match `*`) → commit
  (overwrite marker) → get/release; SHA-256 blob naming; commit never released (Principle IV).
  *(Depends on T013.)*

**Checkpoint:** Core compiles; queue, DLQ, and idempotency available.

---

## Phase 6: Decision core, hosts & DI — see [contracts/08-polling-submit.md](contracts/08-polling-submit.md), [contracts/09-dependency-injection.md](contracts/09-dependency-injection.md)

- [ ] T017 [P] [Core] Create `Polling/PollProcessingResult.cs` (+ `PollAction`) and
  `Polling/SubmitProcessingResult.cs` (+ `SubmitAction`).
- [ ] T018 [US3] Create `Polling/PrintJobService.cs` — allow-list enforcement
  (`ReportPathNotAllowedException`), `SubmitAsync`, `ScheduleFirstPollAsync`, `SubmitAndTrackAsync`.
  *(Depends on T012, T014.)*
- [ ] T019 [US3] Create `Polling/PollProcessor.cs` — poison protection, status fetch (timed),
  complete/reschedule(back-off capped)/dead-letter rules, envelope builder (Principle II).
  *(Depends on T012, T017.)*
- [ ] T020 [US2] Create `Polling/SubmitProcessor.cs` — poison protection, claim→submit→commit→schedule,
  duplicate handling (`SubmitClaimPendingException`), claim release on pre-commit failure (Principle IV).
  *(Depends on T016, T017, T018.)*
- [ ] T021 [US3] Create `Polling/PrintJobPollingWorker.cs` — `BackgroundService` adapter over
  `PollProcessor` with bounded parallelism (channels). *(Depends on T019.)*
- [ ] T022 [Core] Create `DependencyInjection/ServiceCollectionExtensions.cs` —
  `AddOracleBiUniversalPrint` (options+validation, typed HttpClients, singletons, processors) and
  `AddPrintPollingWorker`. *(Depends on T008–T021.)*

**Checkpoint:** Core builds warning-clean; full decision core + in-process host wired.

---

## Phase 7: Verification (unit tests) — see [contracts/11-tests.md](contracts/11-tests.md)

**REQUIRED by Principle VII. Can start as soon as the units under test exist.**

- [ ] T023 [P] [US3] `tests/.../UniversalPrintStatusMapperTests.cs` — completed/failed/printing mapping.
  *(Depends on T011.)*
- [ ] T024 [P] [US1] `tests/.../PrintSecurityOptionsTests.cs` — allow-list allow/deny, case-insensitive,
  empty=allow-all. *(Depends on T007.)*
- [ ] T025 [P] [US3] `tests/.../PollProcessorTests.cs` — completed/failed/reschedule/poll-timeout/
  max-delivery + transient rethrow. *(Depends on T019.)*
- [ ] T026 [P] [US2] `tests/.../SubmitProcessorTests.cs` — fresh submit, committed duplicate,
  uncommitted duplicate, rejected path, pre-submit failure release, max-delivery. *(Depends on T020.)*

**Checkpoint:** `dotnet test` passes; the pure core is covered without Azure dependencies.

---

## Phase 8: Serverless host — see [contracts/10-azure-functions.md](contracts/10-azure-functions.md)

- [ ] T027 [Setup] Create `src/OracleBi.UniversalPrint.Functions/host.json`
  (`telemetryMode=OpenTelemetry`, queues config) and `local.settings.json` (Azurite + all sections).
- [ ] T028 [Setup] Create `src/OracleBi.UniversalPrint.Functions/Program.cs` — HostBuilder,
  `AddOracleBiUniversalPrint`, single OTel pipeline `UseAzureMonitorExporter` (no classic SDK).
  *(Depends on T022.)*
- [ ] T029 [US1] Create `SubmitPrintJobFunction.cs` — HTTP `POST print-jobs`, 400/403 validation,
  correlation + idempotency keys, enqueue submit, return 202. *(Depends on T014, T028.)*
- [ ] T030 [US2] Create `RenderAndSubmitFunction.cs` — submit-queue trigger adapter over
  `SubmitProcessor`; poison → DLQ. *(Depends on T020, T028.)*
- [ ] T031 [US3] Create `PollPrintJobFunction.cs` — poll-queue trigger adapter over `PollProcessor`;
  poison → DLQ; transient → throw for runtime retry. *(Depends on T019, T028.)*
- [ ] T032 [US4] Create `DeadLetterMonitorFunction.cs` — DLQ trigger; `LogCritical` alert; optional
  adaptive-card webhook omitting sensitive detail; retry on webhook failure. *(Depends on T028.)*

**Checkpoint:** `func start` (with Azurite) lists four functions; `POST /api/print-jobs` returns 202.

---

## Phase 9: Deployment & docs — see [contracts/12-infra-and-docs.md](contracts/12-infra-and-docs.md)

- [ ] T033 [P] [Infra] Create `azure.yaml` (azd; functions service → Functions host) and
  `infra/bicepconfig.json` (enable Microsoft Graph Bicep extension).
- [ ] T034 [Infra] Create `infra/resources.bicep` — AVM modules for Log Analytics, App Insights,
  Key Vault (Oracle BI password secret), Storage (queues + containers, shared-key disabled), Flex
  Consumption plan, Function app (identity, deployment container, dotnet-isolated 10.0, app settings),
  Queue + Key Vault role assignments. *(Depends on solution building, T027–T032 for settings parity.)*
- [ ] T035 [Infra] Create `infra/graph-roles.bicep` (Graph app roles `PrintJob.ReadWrite.All`,
  `Printer.Read.All` to the managed identity) and `infra/network.bicep` (opt-in VNet + private DNS).
  *(Depends on T034 outputs.)*
- [ ] T036 [Infra] Create `infra/main.bicep` (subscription scope: RG + module wiring + outputs) and
  `infra/main.parameters.json`. *(Depends on T034, T035.)*
- [ ] T037 [P] [Infra] Create/refresh `README.md` — architecture, retry/telemetry best practices,
  polling/DLQ design, monitoring/alerting (KQL + metric + queue-depth), correlation, setup, local run,
  hosting decision, security hardening, `azd up` steps.

**Checkpoint:** `az bicep build --file infra/main.bicep` succeeds; `azd up` provisions + deploys.

---

## Dependencies & Execution Order

- **Phase 1 → 2 → 3** are sequential foundations.
- **Phase 4** depends on 2–3; **Phase 5** depends on 2–3; **Phase 6** depends on 4–5.
- **Phase 7** tests depend only on their unit under test (can begin mid-Phase 4/6).
- **Phase 8** depends on Phase 6 (DI) and Phase 5 (queue/DLQ).
- **Phase 9** depends on a building solution and the Functions app settings (Phase 8).

## Parallel Execution Examples
- After T005: run **T002–T004** project creations are already done; begin **T006 [P]** and **T007 [P]**
  together (different folders).
- In Phase 4: **T010 [P]** (Oracle BI) and **T011 [P]** (status mapper) in parallel; T012 waits on T011.
- In Phase 5: **T014/T015/T016** are independent once **T013** lands (different files).
- In Phase 7: **T023–T026 [P]** run together once their units exist.

## Implementation Strategy

- **MVP first:** Phases 1–6 + T029/T030/T031 deliver US1–US3 (submit → render → print → track).
  US4 (T015/T032 + telemetry already in core) adds operability.
- Build warning-clean and run tests at every checkpoint (Principles VII + Development Workflow).
- Each task references its originating contract in [contracts/](contracts/) for the exact code-level details.
