# Feature Specification: Oracle BI → Microsoft Universal Print provider

**Feature Branch:** `001-oraclebi-universalprint`
**Created:** 2026-06-25
**Status:** Implemented (reverse-engineered from the existing solution)
**Input:** Print rendered Oracle BI Publisher reports to a Microsoft Universal Print printer with
production-grade reliability, security, and observability.

## Clarifications

### Session 2026-06-25
- Q: How is a caller authorized to print a given report? → A: Defence-in-depth — endpoint
  authentication plus an operator-managed report-path allow-list; disallowed paths are rejected
  synchronously with `403`.
- Q: What happens when a print job never reaches a terminal state? → A: It is re-polled with capped
  exponential back-off up to a bounded attempt budget, then dead-lettered with reason
  `PollTimeoutExceeded`.
- Q: How are duplicate submissions (client retry or queue redelivery) handled? → A: An idempotency key
  ensures the physical print is created at most once; duplicates re-drive tracking, never re-print.
- Q: How are operators notified of failures? → A: Both a metric (`print.deadletter.count`) and a
  correlated log event, plus an optional near-real-time webhook (Teams/Slack/Logic App) per event.

## User Scenarios & Testing

### User Story 1 - Submit a report for printing (Priority: P1)

A business application submits a request to print an Oracle BI Publisher report (e.g. a finance
invoice) to a shared Universal Print printer and immediately receives a tracking id, without waiting
for the render and upload to complete.

**Why this priority:** this is the core value of the integration — turning a report request into a
physical print reliably and asynchronously. Nothing else matters if this does not work.

**Independent Test:** POST a valid report request to the submit endpoint; assert a `202 Accepted` with
a `correlationId` and `idempotencyKey` is returned within milliseconds, and that a submit message is
enqueued for background processing.

**Acceptance Scenarios:**
1. **Given** a valid report request with an allow-listed report path, **When** it is submitted,
   **Then** the system responds `202 Accepted` with a `correlationId` and `idempotencyKey` and queues
   the work for asynchronous render + submit.
2. **Given** a request whose `reportPath` is missing or whose body is invalid, **When** it is
   submitted, **Then** the system responds `400 Bad Request` and queues nothing.
3. **Given** a request whose `reportPath` is not on the allow-list, **When** it is submitted, **Then**
   the system responds `403 Forbidden` synchronously and queues nothing.

### User Story 2 - Reliable asynchronous render and submit (Priority: P1)

The system renders the report from Oracle BI Publisher and submits it to Universal Print off the
request thread, surviving transient failures and never producing a duplicate physical print.

**Why this priority:** correctness of the actual print — exactly once, resilient to transient cloud
failures — is the integration's reliability contract.

**Independent Test:** enqueue a submit message and, using fakes for Oracle BI and Graph, assert the
report is rendered once, a single Universal Print job is created, an idempotency claim is committed,
and a first status poll is scheduled; redelivering the same message creates no second print.

**Acceptance Scenarios:**
1. **Given** a fresh submit message, **When** processed, **Then** the report is rendered, a single
   Universal Print job is created, the idempotency key is committed, and the first poll is scheduled.
2. **Given** a redelivered submit message whose key is already committed, **When** processed, **Then**
   no new print is created and tracking (polling) is (re-)driven for the existing job.
3. **Given** a transient failure before the print job is created, **When** processed, **Then** the
   claim is released and the message is retried by the queue.
4. **Given** repeated transient failures exceeding the delivery-attempt budget, **When** processed,
   **Then** the message is dead-lettered with reason `MaxDeliveryAttemptsExceeded`.

### User Story 3 - Track a job to completion (Priority: P1)

The system polls Universal Print for job status and drives each job to a terminal outcome: completed,
failed, or dead-lettered.

**Why this priority:** without status tracking the system cannot report success, detect failure, or
alert — the job would be fire-and-forget.

**Independent Test:** feed a poll message through the processor with a stubbed status provider and
assert the correct action (complete / reschedule with back-off / dead-letter) for each status.

**Acceptance Scenarios:**
1. **Given** a job that Universal Print reports as completed, **When** polled, **Then** the poll
   message is deleted and a completion metric + end-to-end duration are recorded.
2. **Given** a job still printing under the attempt budget, **When** polled, **Then** a new poll is
   scheduled with capped exponential back-off and an incremented attempt count.
3. **Given** a job Universal Print reports as failed/aborted, **When** polled, **Then** it is
   dead-lettered with reason `PrintJobFailed`.
4. **Given** a job that never reaches a terminal state within the attempt budget, **When** polled,
   **Then** it is dead-lettered with reason `PollTimeoutExceeded`.

### User Story 4 - Operate, alert, and correlate failures (Priority: P2)

An operator is notified when a job is dead-lettered and can trace any dead-letter event back to the
full job history using a single correlation id.

**Why this priority:** failures are inevitable; fast, correlated visibility is what makes the system
operable in production. It is P2 because the happy path (P1) must exist first.

**Independent Test:** write a dead-letter envelope and assert a `print.deadletter.count` metric and a
structured `DEAD-LETTER print job` log are emitted carrying the correlation id, reason, and printer,
and that an optional webhook receives an adaptive card omitting sensitive detail.

**Acceptance Scenarios:**
1. **Given** any dead-letter event, **When** it is written, **Then** both a metric (split by reason
   and printer) and a correlated structured log are emitted.
2. **Given** a configured notification webhook, **When** a job is dead-lettered, **Then** an adaptive
   card with the correlation id and reason — but no sensitive error body — is posted near-real-time.
3. **Given** a correlation id from any dead-letter event, **When** queried in the telemetry store,
   **Then** the complete job timeline (submit, renders, Graph calls, polls, dead-letter) is returned.

### Edge Cases
- A poll or submit message body that cannot be deserialized (poison message) is dead-lettered with
  reason `DeserializationFailure` rather than retried forever.
- A submit duplicate whose claim exists but is not yet committed is deferred to a retry (it recovers
  once the in-flight attempt commits, or dead-letters on max delivery), never silently dropped.
- An Oracle BI base URL that is not HTTPS (with insecure transport disallowed) fails fast at startup.
- A report renders to an empty or oversized document; chunked upload must handle documents of any
  size in Graph-compliant chunks.
- The notification webhook itself fails; the notification is retried so alerting stays reliable.

## Requirements

### Functional Requirements
- **FR-001:** The system MUST expose an endpoint that accepts a print request (report path, output
  format, document name, parameters, optional printer id) and returns a tracking `correlationId`.
- **FR-002:** The submit endpoint MUST validate the request and reject malformed requests with `400`
  and disallowed report paths with `403`, queuing nothing in either case.
- **FR-003:** The system MUST accept the request asynchronously (`202`) and perform render + submit on
  a background queue, not on the request thread.
- **FR-004:** The system MUST render the requested report from Oracle BI Publisher to document bytes.
- **FR-005:** The system MUST submit the rendered document to a Universal Print printer (create job →
  upload document in compliant chunks → start job) via Microsoft Graph.
- **FR-006:** The system MUST create at most one physical print per idempotency key, regardless of
  client retries or queue redeliveries.
- **FR-007:** The system MUST poll Universal Print for job status and drive each job to completion,
  reschedule (back-off), or dead-letter.
- **FR-008:** The system MUST dead-letter messages for: max delivery attempts exceeded, deserialization
  failure, permanent print failure, submit rejection, and poll-timeout exceeded — each with a distinct
  reason code and enough context to correlate and replay.
- **FR-009:** The system MUST support two interchangeable polling hosts (in-process background worker
  and serverless queue trigger) sharing identical decision logic.
- **FR-010:** The system MUST gate printable reports by an operator-configurable allow-list of report
  paths; an empty list allows all.
- **FR-011:** The system MUST emit metrics for jobs submitted, completed, failed, poll attempts, poll
  latency, end-to-end duration, and dead-letter count, dimensioned by printer and (where relevant)
  reason/state.
- **FR-012:** The system MUST stamp a single correlation id on the job, all traces, queue messages, and
  the dead-letter envelope, enabling end-to-end correlation from one id.
- **FR-013:** The system MUST provide optional near-real-time outbound notification of dead-letter
  events via a configurable webhook, omitting sensitive content.
- **FR-014:** The system MUST never log secrets or dependency response bodies at error level, embed
  them in exceptions/DLQ envelopes, or include them in webhook payloads.
- **FR-015:** The system MUST authenticate to dependencies using managed identity where deployed to
  Azure (Storage, Key Vault, Graph) and read the Oracle BI password from a secret store.
- **FR-016:** The system MUST be deployable to Azure with one command, provisioning all required
  resources and identity-based access.

### Key Entities
- **Report request** — what to print: report path, output format, document name, parameters, optional
  target printer.
- **Print job** — a tracked unit of work: correlation id, request, printer id, Universal Print job id,
  state, last error, timestamps.
- **Submit message** — queued instruction to render + submit: correlation id, idempotency key, request.
- **Poll message** — queued instruction to check status once: correlation id, printer id, Universal
  Print job id, poll-attempt count, scheduled time.
- **Idempotency record** — durable proof a print was created: correlation id, Universal Print job id,
  printer id, committed timestamp.
- **Dead-letter envelope** — a permanently failed/poison message: correlation id, reason code, printer,
  Universal Print job id, delivery attempts, error detail, original message, timestamp.
- **Job status** — normalized state (printing/completed/failed/abandoned) plus raw provider detail.

## Success Criteria

### Measurable Outcomes
- **SC-001:** A submit request returns a tracking id in under ~250 ms at the endpoint (work is
  asynchronous).
- **SC-002:** No duplicate physical print is ever produced for a single idempotency key, including
  under client retries and queue redeliveries (verified by tests and idempotency design).
- **SC-003:** 100% of dead-letter events are both alertable (metric) and correlatable (log carrying the
  correlation id).
- **SC-004:** Any dead-letter event can be traced to the complete job timeline using only its
  correlation id.
- **SC-005:** Jobs that never reach a terminal state are bounded — none poll indefinitely; all
  terminate in completion or a dead-letter within the configured attempt budget.
- **SC-006:** Transient dependency failures do not cause job loss or duplication; they are retried
  within bounds and surfaced if permanent.
- **SC-007:** The solution deploys to Azure with a single command and runs without any client secret
  (identity-based access to Storage, Key Vault, and Graph).

## Assumptions
- Oracle BI Publisher and Microsoft Graph are reachable public REST endpoints; the app makes only REST
  calls (no native dependencies), justifying a serverless host.
- A registered/shared Universal Print printer id is available, and an Entra ID identity holds the
  Universal Print application permissions (admin-consented).
- Continuous polling means the poll queue rarely fully drains; scale-to-zero is not expected to be
  continuous, and this trade-off is accepted for cost efficiency versus an always-on plan.
