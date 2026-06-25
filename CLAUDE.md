# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

Dark Software Factory (DSF) is the **blueprint**, not a running factory. It's a template
plus tooling that stamps out an isolated "software factory" per product: software that
decides what to build (Feature Council), builds it (Coding Squad), and operates it (SRE
Agent), with people governing from outside the loop. This repo mostly implements the
**Feature Council** phase plus the provisioning CLI and Control Center; Coding Squad and
SRE Agent are specced in `docs/site/concept/` and `docs/adr/` and partly delegated to Azure.

## Commands

Uses `uv`. Run everything through `uv run`. `make install` is `uv sync --all-packages`.

- Install: `make install` (or `uv sync --all-packages`)
- Test (all members): `make test` (`uv run pytest -q`)
- Single test: `uv run pytest core/tests/test_foo.py::test_bar -q`
- Lint: `make lint` (`uv run ruff check .`); autofix: `make fmt`
- Import boundaries: `make lint-imports` (`uv run lint-imports`) — enforces the
  architectural contracts in `pyproject.toml` `[tool.importlinter]`. Run this after any
  cross-member import change; it gates CI.

CI (`.github/workflows/ci.yml`) runs, in order: ruff → lint-imports → pytest. All three
must pass.

## Workspace layout

A `uv` workspace (`pyproject.toml` `[tool.uv.workspace]`) with four members, all sharing
one PEP 420 namespace package `dsf` (no member ships its own top-level package):

- **core/** (`dsf-core`) — shared base: `contracts`, `ports`, `config`, `memory`, `model`,
  `observability`, `a2a`, `container`, `learning`, `github_client`. Imports no
  application member.
- **feature-council/** (`dsf-feature-council`) — the runtime: `agents`, `council`,
  `orchestrator`, `triggers`, `evals`, `runtime`. Console script `dsfctl`.
- **cli/** (`dsf-cli`) — factory CLI + instance provisioning: `cli`, `instance`. Console
  script `dsf`.
- **control-center/** (`dsf-control-center`) — governance web UI (FastAPI + Jinja).
  Console script `dsf-control-center`.

**Import rule (enforced):** `core` must not import any application member; the three
application members (`cli`+`instance`, `feature-council`'s packages, `control_center`) must
not import each other. Contracts are expressed over top-level `dsf.*` subpackages — see the
`forbidden`/`source` module lists in `pyproject.toml`.

## Architecture

### The conveyor (Feature Council)

The heart is a 7-station pipeline in `feature-council/src/dsf/orchestrator/`. `conveyor.run_line`
drives stations S1..S7 in order over a `Run`:

1. **s1_triage** — debounce/dedup; can KILL a run (stops the line early)
2. **s2_investigation** — gather evidence from source agents
3. **s3_synthesis** — turn evidence into proposals
4. **s4_grounding** — force every claim to trace to real evidence
5. **s5_council** — critics deliberate and vote
6. **s6_routing** — apply label taxonomy / routing policy
7. **s7_filing** — file de-duplicated GitHub issues (skipped on dry-run)

State persists to the **`Blackboard`** (`orchestrator/blackboard.py`) after each station,
backed by the `MemoryStore` port. Each station records an idempotent **checkpoint**, so a
re-run resumes past completed stations. Terminal statuses (`KILLED`, `FILED`, `ERROR`) are
never re-driven. Any per-station exception is converted to an `ERROR` terminal state
(audited, persisted) rather than propagated. The contract types (`Run`, `Proposal`,
`EvidenceItem`, `CouncilVerdict`, etc.) and enums (`RunStatus`, `SourceKind`, `Verdict`,
...) live in `core/src/dsf/contracts/`.

### Council

`council/` holds the deliberation machinery: `critics/` (cost, duplication, feasibility,
grounding, security, strategic_fit, value), plus `jury`, `deliberation`, `synthesizer`,
`decision`, `outcome`. Critic weights and which critics are enabled are runtime-governable
config, not code.

### Source agents (A2A)

`agents/` has one subpackage per `SourceKind` (sentry, grafana, foundryiq, webiq,
incidents, azuremonitor). They're served over A2A (`core/dsf/a2a`) and discovered via
`agents/registry.py` (`DEPLOYABLE_AGENTS`, keyed on `SourceKind.value.lower()`). The
registry holds **path strings only** so importing it has no side effects.

### Ports + real-only `src/` (DI)

External dependencies are `typing.Protocol` ports in `core/src/dsf/ports/` (`ModelClient`,
`MemoryStore`, `ConfigStore`, `GitHubClient`, `Tracer`, `EmbeddingClient`).
`core/src/dsf/container.py` `build_services()` wires a `Services` bundle of the **real**
Azure adapters only (App Configuration, Cosmos, Azure OpenAI, the OTel tracer, the real
GitHub client). There is **no `mode`**: it requires `DSF_PRODUCT` plus every Azure endpoint
and raises (naming what is unset) if anything is missing. It never falls back to a stub.

**Real-only rule (ADR 0014, supersedes 0005):** `src/` ships only real implementations.
The deterministic doubles (`InMemory*` / `Deterministic*` / `Recording*` / `NoOp*`) live in
the `testing/dsf_testing/` package; tests build a bundle with
`dsf_testing.build_test_services()`. Do not put stubs, fixtures, offline fallbacks, or
`Fake*` names in `src/`. Out-of-scope work is removed and tracked as a GitHub issue, never
left as a stub (e.g. the tickets agent → issues #60/#61). `InMemoryConfigStore` is the one
double still in `src/` (the source agents default to it) and is on its way out under #61.

### Entry points

- `dsfctl` (`runtime/control.py`) — operate a running instance: `run --signal <json>`
  (executes for real; `--dry-run` previews), `sweep`, `serve-orchestrator` (one tick:
  sweep sources), `serve-agent --kind <kind>`. DSF is pull-only: it gets work by sweeping
  source agents, not from a pushed signal inbox (a durable push queue is future scope).
- `dsf new` (`cli/factory.py`) — provision an isolated product factory (GitHub repo +
  Coding Squad, Azure resource group, ACA-hosted runtime). Executes by default; `--dry-run`
  produces a what-if preview, `--write-plan` persists the manifest under `config/instances/`.
  Provisioning logic is in `cli/src/dsf/instance/`.

## Testing conventions

- Tests live under each member's own `tests/` dir (`<member>/tests/`). pytest uses
  `--import-mode=importlib` with namespace packages (set in root `pyproject.toml`) because
  every member's test root is named `tests` and would otherwise collide.
- Shared, dependency-light test doubles/builders live in `testing/dsf_testing/` (on the
  pytest `pythonpath`); import them with `from dsf_testing import ...`.
- `asyncio_mode = "auto"` — async tests need no decorator.

## Conventions

- **Commit messages** follow Conventional Commits: start every message with one of
  `feat:` (new capability), `fix:` (bug fix), `chore:` (deps/tooling/housekeeping),
  `docs:` (docs, ADRs, or `docs/site/` content), `refactor:` (behavior-preserving
  restructure), `test:` (tests only), `ci:` (changes under `.github/workflows/`),
  `perf:` (performance), or `build:` (packaging — `pyproject.toml`/`uv`). Use the
  imperative mood after the prefix, e.g. `feat: add azuremonitor source agent`.
- `uv` only; never call bare `python`/`pip`/`pytest`.
- ruff: line length 100, target py312, rules `E,F,I,UP,B`.
- All I/O-bearing port methods are `async`.
- Architecture decisions are recorded in `docs/adr/`; read the relevant ADR before
  reworking a subsystem (e.g. 0014 real-only `src/` + pull-only, 0007 council↔squad handoff,
  0010 uv workspace, 0011 deliberative council). Phase write-ups are in `docs/site/concept/`; the
  operational runbook is `docs/site/get-started/operate.md`.
