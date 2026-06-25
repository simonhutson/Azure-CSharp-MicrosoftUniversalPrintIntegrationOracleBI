# Feature Specification: Analyzer hardening to `latest-recommended`

**Feature Branch:** `002-analyzer-hardening`
**Created:** 2026-06-25
**Status:** In progress
**Input:** Raise the solution's static-analysis posture from the default analysis mode to
`latest-recommended`, with a warning-clean, warnings-as-errors build.

## Why

The maintainability baseline (feature `001`, `Directory.Build.props`) enabled .NET analyzers in the
**Default** mode so the build stayed green. The team prefers the stricter **`latest-recommended`**
posture, which enforces additional performance, design, naming, and globalization rules. Adopting it
improves consistency and catches a class of issues (e.g. culture-sensitive formatting, allocation in
logging) earlier — but it surfaces 146 pre-existing findings that must be resolved or explicitly,
defensibly suppressed.

## Scope

In scope: resolving every `latest-recommended` finding so the solution builds warning-clean with
`TreatWarningsAsErrors=true`, and flipping the analysis mode. Out of scope: any change to runtime
behaviour, public API shape, logging output text, or package versions.

## User Scenarios & Testing

### User Story 1 - Stricter analysis enforced in build (Priority: P1)

As a maintainer, I want the build to enforce `latest-recommended` analyzers so new code is held to a
higher, consistent standard automatically.

**Independent Test:** build the solution with `latest-recommended` + warnings-as-errors and confirm
zero warnings/errors; run the test suite and confirm all tests still pass.

**Acceptance Scenarios:**
1. **Given** the analysis mode is `latest-recommended`, **When** `dotnet build -c Release` runs,
   **Then** it completes with 0 warnings and 0 errors.
2. **Given** the refactor is complete, **When** `dotnet test` runs, **Then** all existing tests pass
   unchanged (no behavioural regression).

### User Story 2 - Logging performance pattern (Priority: P2)

As a maintainer, I want logging to use compile-time `LoggerMessage` delegates so log calls don't
allocate or evaluate arguments when the level is disabled.

**Independent Test:** confirm `CA1848`/`CA1873` no longer report, and that log message templates and
levels are unchanged from before.

**Acceptance Scenarios:**
1. **Given** a logging call site, **When** built under `latest-recommended`, **Then** no `CA1848` or
   `CA1873` is raised and the emitted message template/level matches the previous behaviour.

### Edge Cases
- A logging class that is `static` (e.g. `ResiliencePipelines`) cannot use instance source-generated
  partial methods — it uses static `[LoggerMessage]` partial methods taking the `ILogger` parameter.
- Test method names intentionally use underscores (`Method_Scenario_Expected`); these are exempt, not
  renamed.
- Type names ending in `Queue` are domain-accurate and must not be renamed.

## Requirements

### Functional Requirements
- **FR-001:** The solution MUST build warning-clean under `latest-recommended` with
  `TreatWarningsAsErrors=true`.
- **FR-002:** All existing unit tests MUST pass unchanged.
- **FR-003:** Logging output (message templates, levels, structured property names) MUST be preserved.
- **FR-004:** No runtime behaviour, public API, or package version may change.
- **FR-005:** Any rule that is suppressed rather than fixed MUST be suppressed at the narrowest
  reasonable scope with a documented rationale.

### Resolution policy (per rule)
| Rule | Count | Resolution |
| --- | --- | --- |
| CA1848 (LoggerMessage delegates) | 68 | **Fix** — convert call sites to `[LoggerMessage]` partial methods. |
| CA1873 (guard expensive log args) | 18 | **Fixed by CA1848** — generated delegates defer arg evaluation. |
| CA1707 (underscores in names) | 42 | **Suppress** in the test project only (xUnit naming convention). |
| CA1711 (`Queue` suffix) | 8 | **Suppress** (`none`) — domain-accurate naming. |
| CA1861 (constant array args) | 6 | **Fix** — hoist to `static readonly` fields (test code). |
| CA1305 (IFormatProvider) | 2 | **Fix** — use `CultureInfo.InvariantCulture`. |
| CA1822 (mark static) | 2 | **Decide** — keep instance API; suppress for the member with rationale. |

## Success Criteria

### Measurable Outcomes
- **SC-001:** `dotnet build -c Release` → 0 warnings, 0 errors under `latest-recommended`.
- **SC-002:** `dotnet test` → all tests pass (same count as before, currently 31).
- **SC-003:** No change to log message templates/levels (diff shows only mechanical call-site changes).
- **SC-004:** Every suppression is scoped and carries a one-line rationale.

## Assumptions
- Source-generated `LoggerMessage` is available (.NET 10) and acceptable; affected classes may be made
  `partial`.
- Suppressing `CA1707` for tests and `CA1711` for domain types is an accepted team policy.
