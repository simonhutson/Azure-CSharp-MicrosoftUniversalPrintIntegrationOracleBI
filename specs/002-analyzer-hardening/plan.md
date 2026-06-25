# Implementation Plan: Analyzer hardening to `latest-recommended`

**Branch:** `002-analyzer-hardening` | **Date:** 2026-06-25 | **Spec:** [spec.md](spec.md)

## Summary

Resolve all 146 `latest-recommended` analyzer findings — primarily by converting 68 logging call
sites to source-generated `[LoggerMessage]` delegates (which also clears 18 `CA1873`), plus a handful
of small fixes and three documented, narrowly-scoped suppressions — then switch the build to
`latest-recommended`. No runtime behaviour, public API, logging text, or package versions change.

## Technical Context

**Language/Version:** C# `latest`, .NET 10
**Analyzers:** in-box .NET analyzers; mode raised from `Default` to `Recommended`
(`latest-recommended`) in [Directory.Build.props](../../Directory.Build.props)
**Testing:** existing xUnit suite (31 tests) must pass unchanged
**Constraints:** preserve log templates/levels; no behavioural change; suppressions scoped + documented

## Constitution Check

| Principle | Compliance |
| --- | --- |
| VI. Observability by Default | Log templates, levels, and structured property names are preserved; only the call mechanism changes. The DLQ metric + `DEAD-LETTER print job` log are unchanged. |
| VII. Test the Pure Core | All existing tests pass unchanged; no test logic altered (only naming-rule suppression + array hoisting). |
| Additional Constraints (warning-clean build) | Strengthened: the build now enforces `latest-recommended` warning-clean. |

**Result:** PASS — this feature reinforces existing principles; no behavioural deviation.

## Approach

### LoggerMessage conversion pattern (CA1848 + CA1873)
Each logging class becomes `partial` and declares private partial methods:

```csharp
public sealed partial class PollProcessor
{
    [LoggerMessage(Level = LogLevel.Information,
        Message = "Print job {correlationId} completed (UP job {jobId}).")]
    private partial void LogJobCompleted(string correlationId, string jobId);
}
```

For the `static` `ResiliencePipelines` class, use static partial methods that take the `ILogger`:

```csharp
[LoggerMessage(Level = LogLevel.Warning,
    Message = "Transient failure on attempt {attempt}. Retrying in {delay}. Status={status}")]
static partial void LogTransientRetry(ILogger logger, int attempt, TimeSpan delay, HttpStatusCode? status);
```

Message text and level are copied verbatim from the current calls so output is identical.

### Small fixes
- **CA1305:** `int.ToString()` → `int.ToString(CultureInfo.InvariantCulture)` in the DLQ monitor card.
- **CA1861:** hoist inline constant arrays in tests to `private static readonly` fields.

### Documented suppressions
- **CA1707:** `<NoWarn>$(NoWarn);CA1707</NoWarn>` in the test `.csproj` (xUnit naming convention).
- **CA1711:** `dotnet_diagnostic.CA1711.severity = none` in `.editorconfig` (domain-accurate `Queue`).
- **CA1822:** keep `PrintTelemetry.StartActivity` as instance API; suppress CA1822 for that member
  with a rationale (it is part of the telemetry instance surface).

### Final switch
Flip [Directory.Build.props](../../Directory.Build.props): `AnalysisMode=Default` →
`AnalysisMode=Recommended` (equivalently `AnalysisLevel=latest-recommended`).

## Files affected

**Logging conversions (CA1848/CA1873):** `PollProcessor`, `SubmitProcessor`, `PrintJobService`,
`PrintJobPollingWorker`, `OracleBiClient`, `UniversalPrintProvider`, `ResiliencePipelines`,
`AzureStorageDeadLetterQueue`, `AzureStoragePrintJobQueue`, `SubmitPrintJobFunction`,
`RenderAndSubmitFunction`, `PollPrintJobFunction`, `DeadLetterMonitorFunction`.

**Small fixes:** `DeadLetterMonitorFunction` (CA1305); `SubmitProcessorTests`,
`UniversalPrintStatusMapperTests`, `PollProcessorTests`, `PrintSecurityOptionsTests` (CA1861 where present).

**Config:** test `.csproj` (CA1707), `.editorconfig` (CA1711, remove the temporary advisory category
overrides), `Directory.Build.props` (mode switch), `PrintTelemetry` (CA1822 attribute).

## Complexity Tracking

The only notable cost is breadth (68 call sites across 13 files). Risk is low — output is preserved
and tests guard behaviour — but the change is large and should be reviewed as a focused PR.
