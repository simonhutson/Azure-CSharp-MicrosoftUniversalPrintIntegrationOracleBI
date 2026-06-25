<!--
Sync Impact Report
- Version change: (none) → 1.0.0
- Rationale: Initial ratification, derived from the solution's conventions and the implementation contracts.
- Added sections: Core Principles (I–VIII), Additional Constraints, Development Workflow, Governance
- Removed sections: none
- Templates requiring updates:
  ✅ .specify/templates/plan-template.md (Constitution Check references these articles)
  ✅ .specify/templates/spec-template.md (no change required)
  ✅ .specify/templates/tasks-template.md (no change required)
- Follow-up TODOs: none
-->

# Oracle BI → Universal Print Constitution

These principles govern every spec, plan, task and code change in this repository. They are derived
from the project's established conventions (captured in
`specs/001-oraclebi-universalprint/contracts/`) and are binding on humans and AI agents alike.

## Core Principles

### I. Single Correlation Identity (NON-NEGOTIABLE)
A single `CorrelationId` (`Guid.NewGuid().ToString("N")`) is created at HTTP intake and MUST flow
unbroken through the print job model, every `Activity` (tag `print.correlation_id`), every queue
message, the idempotency record, and the dead-letter envelope. Any new code path that creates,
forwards, or logs work on behalf of a job MUST carry and stamp this id. No log, trace, metric, or
dead-letter event that pertains to a job may omit it.

*Rationale:* end-to-end correlation across submit → render → Graph → poll → DLQ is the foundation of
all diagnostics, alerting, and replay. A single id queried in Application Insights MUST return the
complete timeline for one job.

### II. Host-Agnostic Core
Retry, reschedule, idempotency, and dead-letter rules live in exactly one place: `PollProcessor` and
`SubmitProcessor` in the Core library. Hosts (the in-process `BackgroundService` worker AND the Azure
Functions queue triggers) are thin adapters that translate a host message into a processor call and
apply the returned action. Business rules MUST NOT be duplicated, branched, or special-cased per host.

*Rationale:* the worker and the Functions trigger are interchangeable hosts; divergent logic between
them is a defect that produces inconsistent behaviour in production.

### III. Transient-Only, Bounded Resilience
Outbound calls (Oracle BI, Microsoft Graph, Storage) retry ONLY transient failures (408, 429, 500,
502, 503, 504, `HttpRequestException`, timeouts, socket errors) using exponential back-off WITH
jitter, honouring `Retry-After` when present. Attempts MUST be bounded so a permanently broken
dependency surfaces and is dead-lettered rather than retried forever. Retried POST/PUT requests MUST
rebuild their body per attempt. Shared pipelines in `ResiliencePipelines` are the only sanctioned
retry mechanism.

*Rationale:* unbounded or non-discriminating retries cause retry storms, mask permanent failures, and
corrupt non-idempotent requests.

### IV. Exactly-Once Submission
The Graph print-job creation is irreversible and MUST be guarded by the blob "claim-then-commit"
idempotency store. The first delivery wins the claim; a committed claim is NEVER released, so a
redelivery re-drives polling but can never create a duplicate print. A pre-commit failure releases
the claim so the queue may retry. Uncommitted claims left by a crash are bounded by the submit
queue's visibility timeout and ultimately dead-lettered.

*Rationale:* duplicate physical prints are a user-visible, unrecoverable defect; idempotency is the
only acceptable safeguard.

### V. Security & Least Privilege (NON-NEGOTIABLE)
- Identity-first: prefer managed identity / `DefaultAzureCredential`; connection strings and client
  secrets are permitted only for local development.
- Secrets (e.g. the Oracle BI password) come from Key Vault via references, never plaintext app
  settings committed to source.
- Oracle BI Basic auth requires HTTPS transport (`AllowInsecureTransport=false` by default).
- A report-path allow-list gates what may be printed; violations return `403` synchronously.
- Response bodies and secrets MUST NOT be logged at Error level, embedded in exceptions, written to
  the DLQ envelope, or included in outbound webhook payloads. Correlate via the id in App Insights.
- Storage shared-key access is disabled; SCM/FTP basic publishing is disabled.

*Rationale:* the integration handles potentially sensitive report content and broad Graph print
permissions; defence-in-depth is mandatory.

### VI. Observability by Default
Exactly one `ActivitySource` and one `Meter`, both named `OracleBi.UniversalPrint`, exported to Azure
Monitor via a single OpenTelemetry pipeline (no classic Application Insights SDK — signals exported
once). Every dead-letter event MUST emit BOTH the `print.deadletter.count` metric (for threshold
alerts) AND a structured `LogError` beginning `DEAD-LETTER print job` (for correlated KQL queries).
New behaviours that submit, poll, complete, fail, or dead-letter a job MUST record the corresponding
metric and trace.

*Rationale:* metrics drive fast alerts; correlated logs drive investigation; both are required and
neither is optional.

### VII. Test the Pure Core
Host-agnostic logic — status mapping, the allow-list, and the poll/submit decision rules — MUST be
unit-tested with xUnit using hand-written fakes, with no Azure dependency, Azurite, or network. New or
changed decision logic in `PollProcessor`, `SubmitProcessor`, `UniversalPrintStatusMapper`, or
`PrintSecurityOptions` MUST ship with covering tests.

*Rationale:* the decision rules are where correctness lives; they must be verifiable in isolation and
fast to run.

### VIII. Infrastructure as Verified Code
Infrastructure is `azd`-ready Bicep that composes Azure Verified Modules (AVM) wherever a module
exists; only Graph role grants and the Queue/Key Vault role assignments are raw resources. The deploy
target is Azure Functions on the Flex Consumption plan. Network isolation (private endpoints + VNet
integration) is opt-in and MUST default to off so the standard public deploy keeps working.

*Rationale:* AVM gives reviewed, maintained building blocks; the documented hosting decision keeps the
serverless trade-offs explicit and revisable.

## Additional Constraints

- **Platform:** .NET 10, C# `LangVersion=latest`, `ImplicitUsings` and `Nullable` enabled. The Core
  project sets `TreatWarningsAsErrors=true`; builds MUST be warning-clean.
- **Naming:** root namespace `OracleBi.UniversalPrint` (Functions project
  `OracleBi.UniversalPrint.Functions`); types are `sealed` unless inheritance is required.
- **Dependency baseline:** package versions in
  `specs/001-oraclebi-universalprint/contracts/01-scaffold.md` are the
  known-good baseline; upgrades are allowed only if the solution still builds warning-clean and tests
  pass.
- **Functions model:** isolated worker, `host.json` `telemetryMode=OpenTelemetry`, queue triggers bound
  via `%Queues:*%` settings on the `QueueStorage` connection.

## Development Workflow

1. Changes flow through Spec-Driven Development: `/speckit.specify` → `/speckit.clarify` →
   `/speckit.plan` → `/speckit.tasks` → `/speckit.implement`, with `/speckit.analyze` before
   implementing.
2. Every plan MUST include a Constitution Check confirming compliance with the principles above;
   deviations require an explicit, justified entry in the plan's Complexity Tracking.
3. After each implementation slice, the agent MUST build (`dotnet build`, warning-clean) and run
   (`dotnet test`) before the work is considered done.
4. Spec Kit tooling updates and `specs/` artifact evolution are kept as separate concerns (brownfield
   loop): refresh managed files on upgrade, update `specs/` only when intended behaviour changes.

## Governance

This constitution supersedes ad-hoc conventions. Amendments require: a documented rationale, a version
bump per the policy below, and propagation to dependent templates (`plan`, `spec`, `tasks`) in the
same change. All PRs and AI-driven changes MUST verify compliance; unjustified violations block merge.

**Versioning policy (semantic):**
- MAJOR — removal or redefinition of a principle, or any backward-incompatible governance change.
- MINOR — a new principle/section or materially expanded guidance.
- PATCH — clarifications and wording that do not change obligations.

**Compliance review:** the Constitution Check in every `plan.md` is the enforcement point. Runtime
guard-rails (allow-list, HTTPS enforcement, idempotency, bounded retries) are the executable
expression of these principles and MUST remain in force.

**Version:** 1.0.0 | **Ratified:** 2026-06-25 | **Last Amended:** 2026-06-25
