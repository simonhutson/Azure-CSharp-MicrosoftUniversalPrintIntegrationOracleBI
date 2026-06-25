# Spec Kit artifacts

This folder holds the [Spec Kit](https://github.com/github/spec-kit) Spec-Driven Development artifacts
for this repository. They make the project's intent, design, and work breakdown executable by Spec
Kit's `/speckit.*` commands. The code-level detail that formerly lived in the `prompts/` pack now
lives alongside the plan as per-component implementation contracts under
[`001-oraclebi-universalprint/contracts/`](001-oraclebi-universalprint/contracts/README.md).

## Layout

| Path | Spec Kit role | Produced/refreshed by |
| --- | --- | --- |
| [`../.specify/memory/constitution.md`](../.specify/memory/constitution.md) | Governing principles | `/speckit.constitution` |
| [`001-oraclebi-universalprint/spec.md`](001-oraclebi-universalprint/spec.md) | WHAT / WHY (requirements, user stories, success criteria) | `/speckit.specify`, `/speckit.clarify` |
| [`001-oraclebi-universalprint/plan.md`](001-oraclebi-universalprint/plan.md) | HOW (tech stack, architecture, constitution check) | `/speckit.plan` |
| [`001-oraclebi-universalprint/tasks.md`](001-oraclebi-universalprint/tasks.md) | Actionable, dependency-ordered tasks | `/speckit.tasks` |
| [`001-oraclebi-universalprint/contracts/`](001-oraclebi-universalprint/contracts/README.md) | Code-level implementation contracts (types, signatures, constants, package versions) | hand-authored, referenced by tasks |

## How intent maps across the artifacts

| Concern | Artifact |
| --- | --- |
| Binding conventions & design rules | `constitution.md` (Principles I–VIII) |
| Requirements & behaviour (models, options, flows) | `spec.md` (requirements + key entities) |
| Frameworks, packages, hosts, infra (the *how*) | `plan.md` (Technical Context + structure) |
| Per-component build steps & acceptance | `tasks.md` (T001–T037) → `contracts/NN-*.md` |

## Using these with Spec Kit

If you have not initialized Spec Kit yet:

```powershell
uv tool install specify-cli --from git+https://github.com/github/spec-kit.git
specify init --here --integration copilot
```

`specify init --here` adds the `/speckit.*` commands and the `.specify/` tooling without disturbing
these artifacts. Then, in Copilot Chat:

- `/speckit.analyze` — cross-check `spec.md` ↔ `plan.md` ↔ `tasks.md` ↔ `constitution.md` for gaps.
- `/speckit.tasks` — regenerate or refine the task list if the spec/plan change.
- `/speckit.implement` — execute the tasks (note: the solution is already implemented; use this for
  new features or a from-scratch rebuild in a fresh directory).

## Brownfield note

The solution already exists, so treat these as the baseline. For each new feature or modernization,
add a new numbered feature folder (e.g. `specs/002-<slug>/`) via `/speckit.specify`, keeping Spec Kit
tooling updates separate from `specs/` artifact evolution (see the
[Evolving Specs guide](https://github.com/github/spec-kit/blob/main/docs/guides/evolving-specs.md)).
