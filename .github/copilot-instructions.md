# Copilot instructions — Dark Software Factory (DSF)

DSF is a **blueprint**, not a running factory: a template plus tooling that stamps out an
isolated "software factory" per product (decide what to build → build it → operate it), with
people governing from outside the loop. This repo mainly implements the **Feature Council**
phase plus the provisioning CLI and Control Center; Coding Squad and SRE Agent are specced in
`docs/site/concept/` and `docs/adr/` and partly delegated to Azure.

## Commands

Uses `uv`. Run everything through `uv run` — never call bare `python`/`pip`/`pytest`.

- Install: `make install` (`uv sync --all-packages`)
- Test (all members): `make test` (`uv run pytest -q`)
- **Single test:** `uv run pytest feature-council/tests/<path>/test_foo.py::test_bar -q`
- Lint: `make lint` (`uv run ruff check .`); autofix: `make fmt` (`ruff check --fix .`)
- Import boundaries: `make lint-imports` (`uv run lint-imports`) — run after any cross-member
  import change; it gates CI
- Eval gate: `uv run python -m dsf.evals.runner --gate`

CI (`.github/workflows/ci.yml`) runs in order: **ruff → lint-imports → pytest**. All three
must pass. Lint is `ruff check` only; code need not be `ruff format`-clean.

## Workspace layout

A `uv` workspace (`pyproject.toml` `[tool.uv.workspace]`) with four members, all sharing one
PEP 420 namespace package `dsf` (no member ships its own top-level package). Each member's
source is under `<member>/src/dsf/...`:

- **core/** (`dsf-core`) — shared base: `contracts`, `ports`, `config`, `memory`, `model`,
  `observability`, `a2a`, `signals`, `learning`, plus `container.py`, `github_client.py`.
  Imports no application member.
- **feature-council/** (`dsf-feature-council`) — the runtime: `agents`, `council`,
  `orchestrator`, `triggers`, `evals`, `runtime`. Runs as a module
  (`python -m dsf.runtime.control`); the `dsf` front-door fronts its verbs.
- **cli/** (`dsf-cli`) — factory CLI + instance provisioning: `cli`, `instance`. Console
  script `dsf` (`dsf.cli.factory:main`).
- **control-center/** (`dsf-control-center`) — governance web UI (FastAPI + Jinja). Console
  script `dsf-control-center` (`dsf.control_center.app:main`).

**Import rule (enforced in CI via `lint-imports`):** `core` must not import any application
member; the three application members (`cli`+`instance`, feature-council's packages,
`control_center`) must not import each other. Contracts are over top-level `dsf.*`
subpackages — see `[tool.importlinter]` in `pyproject.toml`.

## Architecture

### The conveyor (Feature Council)

The heart is a 7-station pipeline in `feature-council/src/dsf/orchestrator/`.
`conveyor.run_line` drives stations S1..S7 in order over a `Run`:

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
never re-driven; any per-station exception becomes an audited `ERROR` terminal state rather
than propagating. Contract types (`Run`, `Proposal`, `EvidenceItem`, `CouncilVerdict`) and
enums (`RunStatus`, `SourceKind`, `Verdict`) live in `core/src/dsf/contracts/`.

### Council

`council/` holds the deliberation machinery: `critics/` (cost, duplication, feasibility,
grounding, security, strategic_fit, value), plus `jury`, `deliberation`, `synthesizer`,
`decision`, `outcome`. Critic weights and which critics are enabled are runtime-governable
config, **not code**.

### Source agents (A2A)

`agents/` has one subpackage per `SourceKind` (sentry, grafana, foundryiq, webiq, incidents,
azuremonitor), served over A2A (`core/dsf/a2a`) and discovered via `agents/registry.py`
(`DEPLOYABLE_AGENTS`, keyed on `SourceKind.value.lower()`). The registry holds **path strings
only**, so importing it has no side effects.

### Ports + real-only DI

External dependencies are `typing.Protocol` ports in `core/src/dsf/ports/` (`ModelClient`,
`MemoryStore`, `ConfigStore`, `GitHubClient`, `Tracer`, `EmbeddingClient`); all I/O-bearing
port methods are `async`. `core/src/dsf/container.py` `build_services()` wires the **real**
Azure adapters only. There is **no `mode`**: it requires `DSF_PRODUCT` plus every Azure
endpoint and raises (naming what is unset) if anything is missing — it never falls back to a
stub.

**Real-only rule (ADR 0014, supersedes 0005):** `src/` ships only real implementations. Do
not add stubs, fakes, fixtures, offline fallbacks, or `Fake*`/`InMemory*` names to `src/`.
Deterministic doubles live in `testing/dsf_testing/`; tests build a bundle with
`dsf_testing.build_test_services()`. Out-of-scope work is removed and tracked as a GitHub
issue, never left as a stub.

### Entry points

- `dsf <verb>` (fronts `runtime/control.py` via `python -m dsf.runtime.control`) — operate a running instance: `run --signal <json>`
  (`--dry-run` previews), `sweep`, `serve-orchestrator`, `serve-agent --kind <kind>`. DSF is
  pull-only: it gets work by sweeping source agents, not from a pushed inbox.
- `dsf new` (`cli/factory.py`) — provision an isolated product factory (GitHub repo + Coding
  Squad, Azure resource group, ACA runtime). Executes by default; `--dry-run` previews,
  `--write-plan` persists the manifest under `config/instances/`. Provisioning logic lives in
  `cli/src/dsf/instance/`.

## Conventions

- **Commit messages** follow Conventional Commits: start every message with one of
  `feat:` (new capability), `fix:` (bug fix), `chore:` (deps/tooling/housekeeping),
  `docs:` (docs, ADRs, or `docs/site/` content), `refactor:` (behavior-preserving
  restructure), `test:` (tests only), `ci:` (changes under `.github/workflows/`),
  `perf:` (performance), or `build:` (packaging — `pyproject.toml`/`uv`). Use the
  imperative mood after the prefix, e.g. `feat: add azuremonitor source agent`.
- **Adding a `SourceKind` wires 4 places** (a parity test at
  `feature-council/tests/agents/test_registry.py` enforces it): `contracts/enums.py`
  `SourceKind`, `config/defaults.json` agents block, `agents/registry.py` `DEPLOYABLE_AGENTS`,
  `orchestrator/agent_registry.py` `AGENT_BUILDERS`.
- **Tests** live under each member's own `tests/` dir. pytest uses
  `--import-mode=importlib` with namespace packages (every member's test root is named
  `tests`); `asyncio_mode = "auto"` so async tests need no decorator. Shared doubles/builders
  live in `testing/dsf_testing/` (on the pytest `pythonpath`) — `from dsf_testing import ...`;
  it may import only `dsf.contracts`/`typing`, and runtime `src/` must never import it.
- **Repo-root paths** are computed via `Path(__file__).resolve().parents[N]` — a recurring
  off-by-one risk. Agent backends read root `tests/fixtures/` via `parents[5]`; fixtures stay
  at repo root, not inside members.
- ruff: line length 100, target py312, rules `E,F,I,UP,B`.
- Read the relevant ADR in `docs/adr/` before reworking a subsystem (e.g. 0014 real-only
  `src/` + pull-only, 0007 council↔squad handoff, 0010 uv workspace, 0011 deliberative
  council). Phase write-ups are in `docs/site/concept/`; the operational runbook is
  `docs/site/get-started/operate.md`.
