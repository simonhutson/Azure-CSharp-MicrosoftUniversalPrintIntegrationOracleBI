# Implementation contracts

Code-level contracts that back the tasks in [`../tasks.md`](../tasks.md). Each file specifies the
exact types, signatures, constants, package versions, and behaviours for one component of the
solution. These are the detail layer beneath the spec (WHAT/WHY) and plan (HOW); tasks reference the
contract file that defines what a task must produce.

> Conventions (binding — see [`../../../.specify/memory/constitution.md`](../../../.specify/memory/constitution.md)):
> .NET 10, C# `latest`, `ImplicitUsings` + `Nullable` enabled, Core `TreatWarningsAsErrors=true`,
> root namespace `OracleBi.UniversalPrint`, `sealed` types, one `ActivitySource` + one `Meter` named
> `OracleBi.UniversalPrint`, a single `CorrelationId` (`Guid.NewGuid().ToString("N")`) flowing through
> models/traces/queues/DLQ, identity-first, no secrets/response bodies in logs/DLQ/webhook.

| Contract | Component | Tasks |
| --- | --- | --- |
| [01-scaffold.md](01-scaffold.md) | Solution + 3 projects + package baseline | T001–T005 |
| [02-domain-models.md](02-domain-models.md) | Models + enums | T006 |
| [03-configuration-options.md](03-configuration-options.md) | Options + validation | T007 |
| [04-telemetry-resilience.md](04-telemetry-resilience.md) | `PrintTelemetry`, `ResiliencePipelines` | T008–T009 |
| [05-oraclebi-client.md](05-oraclebi-client.md) | `IOracleBiClient`, `OracleBiClient` | T010 |
| [06-universalprint-provider.md](06-universalprint-provider.md) | Provider + status mapper | T011–T012 |
| [07-queue-idempotency.md](07-queue-idempotency.md) | Queue, DLQ, blob idempotency | T013–T016 |
| [08-polling-submit.md](08-polling-submit.md) | Processors, worker, `PrintJobService` | T017–T021 |
| [09-dependency-injection.md](09-dependency-injection.md) | DI registration | T022 |
| [10-azure-functions.md](10-azure-functions.md) | Functions host + 4 functions | T027–T032 |
| [11-tests.md](11-tests.md) | xUnit unit tests | T023–T026 |
| [12-infra-and-docs.md](12-infra-and-docs.md) | Bicep (AVM) infra, `azure.yaml`, README | T033–T037 |

## Build order & acceptance

Implement in contract order; build (`dotnet build`, warning-clean) after each, and run
`dotnet test` once the test contract lands. Whole-solution acceptance:

- `dotnet build` succeeds with zero warnings (Core treats warnings as errors).
- `dotnet test` passes.
- `func start` runs the Functions host locally against Azurite.
- `azd up` provisions infra and deploys the Functions app.
