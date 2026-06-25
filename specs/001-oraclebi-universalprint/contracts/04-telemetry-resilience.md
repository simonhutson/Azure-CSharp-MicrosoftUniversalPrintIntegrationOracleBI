# Contract 04 — Telemetry & resilience

**Tasks:** T008–T009. Create the cross-cutting telemetry surface and the shared Polly v8 resilience
pipelines. These are used by the Oracle BI client, the Universal Print provider, the queue/idempotency
stores and the poll/submit processors.

## Telemetry — `Telemetry/PrintTelemetry.cs`

A `sealed class PrintTelemetry : IDisposable` exposing one `ActivitySource` and one `Meter`, both
named `OracleBi.UniversalPrint`. Registered as a singleton (contract 09).

- `public const string ActivitySourceName = "OracleBi.UniversalPrint";`
- `public const string MeterName = "OracleBi.UniversalPrint";`
- `public static readonly ActivitySource ActivitySource = new(ActivitySourceName);`
- Construct a `Meter` and create these instruments:

| Member | Instrument | Name | Unit |
| --- | --- | --- | --- |
| `_jobsSubmitted` | `Counter<long>` | `print.jobs.submitted` | `{job}` |
| `_jobsCompleted` | `Counter<long>` | `print.jobs.completed` | `{job}` |
| `_jobsFailed` | `Counter<long>` | `print.jobs.failed` | `{job}` |
| `_pollAttempts` | `Counter<long>` | `print.poll.attempts` | `{attempt}` |
| `_deadLettered` | `Counter<long>` | `print.deadletter.count` | `{message}` |
| `_pollLatencyMs` | `Histogram<double>` | `print.poll.latency` | `ms` |
| `_endToEndSeconds` | `Histogram<double>` | `print.job.duration` | `s` |

Methods:
- `Activity? StartActivity(string name, string correlationId, ActivityKind kind = Internal)` — starts
  the activity and stamps tag `print.correlation_id` = correlationId.
- `JobSubmitted(string printerId)` — counter +1, dim `printer.id`.
- `JobCompleted(string printerId, TimeSpan duration)` — completed +1 (`printer.id`) and record
  `duration.TotalSeconds` on the duration histogram (`printer.id`).
- `JobFailed(string printerId, string reason)` — failed +1, dims `printer.id`, `failure.reason`.
- `PollAttempted(string printerId, double latencyMs, PrintJobState resultState)` — attempts +1
  (dims `printer.id`, `result.state`) and record latency (`printer.id`).
- `DeadLettered(DeadLetterReason reason, string? printerId)` — deadletter +1, dims
  `deadletter.reason`, `printer.id` (fallback `"unknown"`).
- `Dispose()` disposes the meter.

## Resilience — `Resilience/ResiliencePipelines.cs`

A `static class ResiliencePipelines` with Polly v8 (`Polly.Core`). Best practices baked in:
retry only transient failures, exponential back-off **with jitter**, honour `Retry-After`, bounded
attempts.

- `private static readonly HashSet<HttpStatusCode> RetryableStatusCodes` = { 408, 429, 500, 502, 503, 504 }.
- `static bool IsTransientHttp(HttpResponseMessage response)` → contains check.
- `static ResiliencePipeline<HttpResponseMessage> CreateHttpPipeline(ILogger logger, int maxRetries = 5)`:
  - `AddRetry` with `MaxRetryAttempts = maxRetries`, `BackoffType = Exponential`, `UseJitter = true`,
    `Delay = 500ms`, `MaxDelay = 30s`.
  - `ShouldHandle`: exception is `HttpRequestException` or `TimeoutException`, **or** result is a
    transient HTTP status.
  - `DelayGenerator`: if `Outcome.Result?.Headers.RetryAfter?.Delta` is set, return it; else null.
  - `OnRetry`: `LogWarning` with attempt number, delay, status.
- `static ResiliencePipeline CreateOperationPipeline(ILogger logger, int maxRetries = 5)` for queue/blob
  I/O that throws on failure:
  - `AddRetry` with `Exponential`, jitter, `Delay = 250ms`, `MaxDelay = 15s`.
  - `ShouldHandle`: any exception that is **not** `OperationCanceledException`.
  - `OnRetry`: `LogWarning` including the exception.

## Acceptance

- Project compiles. These types have no external dependencies beyond Polly + logging and will be
  consumed by every outbound caller from here on.
