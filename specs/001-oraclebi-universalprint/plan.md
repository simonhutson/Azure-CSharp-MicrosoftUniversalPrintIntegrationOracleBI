# Implementation Plan: Oracle BI → Microsoft Universal Print provider

**Branch:** `001-oraclebi-universalprint` | **Date:** 2026-06-25 | **Spec:** [spec.md](spec.md)
**Input:** Feature specification from `specs/001-oraclebi-universalprint/spec.md`

## Summary

A custom Microsoft Universal Print provider (C# / .NET 10) renders Oracle BI Publisher reports and
submits them to a Universal Print printer via the Microsoft Graph print APIs. The technical approach
is an asynchronous, queue-driven pipeline (HTTP submit → render+submit → status polling) with a
host-agnostic decision core shared by an in-process background worker and Azure Functions queue
triggers, blob-based exactly-once idempotency, Polly v8 transient-only resilience, OpenTelemetry to
Azure Monitor, an explicit dead-letter queue with monitoring, and an `azd`/Bicep (AVM) deployment to
Azure Functions Flex Consumption.

## Technical Context

**Language/Version:** C# `latest`, .NET 10
**Primary Dependencies:** Azure.Identity, Azure.Storage.Queues, Azure.Storage.Blobs, Polly.Core 8,
Microsoft.Extensions.* (Hosting/Http/Options), OpenTelemetry + Azure.Monitor.OpenTelemetry.Exporter,
Microsoft.Azure.Functions.Worker (isolated, v4) + Storage.Queues/Http extensions
**Storage:** Azure Storage Queues (`print-submit`, `print-poll`, `print-poll-deadletter`) and Blobs
(`idempotency` container, `app-package` deployment container); Key Vault for the Oracle BI password
**Testing:** xUnit with hand-written fakes (no Azure/Azurite/network in unit tests)
**Target Platform:** Azure Functions, Flex Consumption plan (Linux, dotnet-isolated 10.0)
**Project Type:** Backend service — Core class library + Functions host + test project
**Performance Goals:** submit endpoint returns a tracking id in <~250 ms (work is asynchronous);
bounded polling back-off capped at the configured max re-poll delay
**Constraints:** identity-first/secret-free in Azure; no secrets or response bodies in logs/DLQ/webhook;
HTTPS-only Oracle BI; report-path allow-list; warning-clean build (Core `TreatWarningsAsErrors`)
**Scale/Scope:** event-driven scaling on queue depth; `maximumInstanceCount` configurable; continuous
polling means the poll queue rarely fully drains

## Constitution Check

*GATE: must pass before implementation. Re-check after design.*

| Principle | How this plan complies |
| --- | --- |
| I. Single Correlation Identity | `CorrelationId` minted in `SubmitPrintJobFunction`, carried on `SubmitMessage`/`PollMessage`/`PrintJob`/`DeadLetterEnvelope` and stamped on every `Activity` via `PrintTelemetry.StartActivity`. |
| II. Host-Agnostic Core | All rules in `PollProcessor`/`SubmitProcessor`; `PrintJobPollingWorker` and the queue-trigger functions are thin adapters. |
| III. Transient-Only, Bounded Resilience | `ResiliencePipelines` (Polly v8) retries only transient HTTP/socket/timeout faults, jittered back-off, honours `Retry-After`, bounded attempts; request bodies rebuilt per attempt. |
| IV. Exactly-Once Submission | `BlobIdempotencyStore` claim-then-commit; committed claims never released; `SubmitProcessor` releases only on pre-commit failure. |
| V. Security & Least Privilege | Allow-list (`403`), HTTPS enforcement, Key Vault password reference, managed identity, no secret/body logging, shared-key + SCM/FTP basic auth disabled. |
| VI. Observability by Default | One `ActivitySource` + one `Meter` (`OracleBi.UniversalPrint`), single OTel pipeline to Azure Monitor; DLQ emits metric + structured `DEAD-LETTER print job` log. |
| VII. Test the Pure Core | xUnit tests for `UniversalPrintStatusMapper`, `PrintSecurityOptions`, `PollProcessor`, `SubmitProcessor` using fakes. |
| VIII. Infrastructure as Verified Code | Bicep composed of AVM modules; Flex Consumption; opt-in network isolation defaulting off. |

**Result:** PASS — no deviations. Complexity Tracking is empty.

## Project Structure

### Documentation (this feature)

```
specs/001-oraclebi-universalprint/
├── spec.md       # WHAT/WHY (requirements, user stories, success criteria)
├── plan.md       # THIS FILE (HOW — tech stack, architecture, constitution check)
└── tasks.md      # Actionable, dependency-ordered task list
```

### Source Code (repository root)

```
src/OracleBi.UniversalPrint.Core/
├── Abstractions/            # IOracleBiClient, IUniversalPrintProvider, IPrintJobQueue,
│                            #   IDeadLetterQueue, IIdempotencyStore
├── Configuration/           # OracleBiOptions, UniversalPrintOptions, QueueOptions,
│                            #   PollingOptions, PrintSecurityOptions
├── Models/                  # PrintJob, PrintJobState, PrintJobStatus, OracleBiReportRequest,
│                            #   OracleBiDocument, SubmitMessage, PollMessage,
│                            #   IdempotencyRecord, DeadLetterEnvelope (+ DeadLetterReason)
├── Telemetry/               # PrintTelemetry (ActivitySource + Meter)
├── Resilience/              # ResiliencePipelines (Polly v8)
├── OracleBiIntegration/     # OracleBiClient (BI Publisher REST)
├── UniversalPrintIntegration/ # UniversalPrintProvider, UniversalPrintStatusMapper
├── Queueing/                # AzureStoragePrintJobQueue, AzureStorageDeadLetterQueue
├── Idempotency/             # BlobIdempotencyStore
├── Polling/                 # PollProcessor, SubmitProcessor, PrintJobService,
│                            #   PrintJobPollingWorker, Poll/SubmitProcessingResult
└── DependencyInjection/     # ServiceCollectionExtensions

src/OracleBi.UniversalPrint.Functions/
├── Program.cs               # HostBuilder + single OpenTelemetry pipeline
├── host.json                # telemetryMode=OpenTelemetry, queues config
├── local.settings.json      # local dev (Azurite) settings
├── SubmitPrintJobFunction.cs    # HTTP 202 + allow-list
├── RenderAndSubmitFunction.cs   # submit-queue trigger → SubmitProcessor
├── PollPrintJobFunction.cs      # poll-queue trigger → PollProcessor
└── DeadLetterMonitorFunction.cs # DLQ trigger → webhook adaptive card

tests/OracleBi.UniversalPrint.Tests/
├── UniversalPrintStatusMapperTests.cs
├── PrintSecurityOptionsTests.cs
├── PollProcessorTests.cs
└── SubmitProcessorTests.cs

infra/                       # main.bicep, resources.bicep, graph-roles.bicep, network.bicep,
                             #   bicepconfig.json, main.parameters.json
azure.yaml                   # azd service mapping (functions)
```

**Structure Decision:** A single backend service split into a reusable Core library and a thin
Functions host, plus a unit-test project. The Core library deliberately has no Functions dependency so
the same logic can be hosted in a Worker Service / App Service / container; the Functions project is
one of two interchangeable hosts (Principle II).

## Architecture & Flow

```
HTTP submit ──► print-submit queue ──► RenderAndSubmit (idempotent) ──► Oracle BI render
                                                │                              │
                                                ▼                       Graph print job
                                          first poll msg ──► print-poll queue ──► PollProcessor
                                                                                     │
                                              completed / reschedule / dead-letter ◄─┘
                                                                                     │
                                          print-poll-deadletter ──► DLQ monitor ──► Teams/webhook
                                          (all signals ──► Application Insights / Azure Monitor)
```

## Phasing

- **Phase 0 — Scaffold & contracts:** solution + 3 projects, package baseline, models, options,
  abstractions. No behaviour yet.
- **Phase 1 — Cross-cutting:** telemetry + resilience.
- **Phase 2 — Integrations:** Oracle BI client, Universal Print provider + status mapper.
- **Phase 3 — Messaging & state:** queue, DLQ, blob idempotency.
- **Phase 4 — Decision core & hosts:** poll/submit processors, `PrintJobService`, background worker,
  DI registration.
- **Phase 5 — Serverless host:** Functions host + four functions + settings.
- **Phase 6 — Verification:** unit tests for the pure core.
- **Phase 7 — Deployment:** Bicep (AVM) infra, `azure.yaml`, docs.

User-story mapping: US1 → Phases 0–3 + `SubmitPrintJobFunction`; US2 → Phases 2–5 (submit path);
US3 → Phases 2–5 (poll path); US4 → DLQ writer + `DeadLetterMonitorFunction` + telemetry + infra.

## Complexity Tracking

*No constitutional deviations. No entries.*
