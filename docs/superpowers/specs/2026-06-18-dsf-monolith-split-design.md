# Split the `dsf` monolith into a uv-workspace of self-contained apps — design (#26)

- Status: Approved
- Date: 2026-06-18
- Issue: #26 (tech-debt). Builds on #25 (agent registry — first slice).
- New ADR: 0010 (supersedes ADR 0001 §1 "single package"; refines ADR 0003).

## Problem

Everything lives in one `src/dsf/**` package — the **factory CLI**, the
**control-center** web app, the **feature-council runtime** (council, orchestrator,
triggers, agents), and the **instance-provisioning** tooling are all intermingled
behind one wheel. Boundaries and ownership are unclear ("currently we have a mess"),
and nothing prevents an app from reaching into another app's internals.

The import graph, however, is cleaner than the layout implies: domain modules import
the composition root's `Services` only as a `TYPE_CHECKING` type hint, and
`build_services` is called by just four entrypoints. The split is therefore largely
**mechanical** — the goal is to make the existing layering explicit, enforced, and
independently buildable.

## Decision

Restructure the repository into a **uv workspace** of four self-contained members at
the repo root, sharing one `dsf.` import namespace via **PEP 420 namespace packages**
and one shared `uv.lock`.

```
dark-software-factory/                 ← uv workspace root
├─ core/             dsf-core                 (third-party deps only)
│   └─ src/dsf/   contracts ports config memory model github_client
│                 observability a2a container learning
├─ feature-council/ dsf-feature-council  ──▶ dsf-core
│   └─ src/dsf/   agents council orchestrator triggers evals runtime
│                 + operator CLI (dsfctl)
├─ cli/             dsf-cli              ──▶ dsf-core            (agent-free)
│   └─ src/dsf/   cli(factory) instance       + factory CLI (dsf new)
└─ control-center/  dsf-control-center   ──▶ dsf-core
    └─ src/dsf/   control_center              + own serve entrypoint
```

### Package boundaries (acyclic: `core ◀ apps`)

- **`dsf-core`** (`core/`) — the shared backbone. Owns: `contracts`, `ports`,
  `config`, `memory`, `model`, `github_client`, `observability`, `a2a`, `container`
  (the `build_services` composition root + `Services` bundle), and `learning`.
  Depends on third-party libraries only. No app depends on another app, so every
  shared type lives here.
  - `learning` lands in core (not feature-council): it imports **only** core, and is
    consumed by **both** feature-council (writes calibration during runs) and
    control-center (reads `proposed_weight_update`). Placing it in core keeps
    control-center a clean core-only peer with no app→app edge.
- **`dsf-feature-council`** (`feature-council/`) — the assembly line. Owns: `agents`,
  `council`, `orchestrator`, `triggers`, `evals` (the line's own quality gate;
  depends on council/orchestrator so it cannot be core), `runtime` (worker
  entrypoint), and the **operator CLI** (`dsfctl`). Depends on `dsf-core`.
- **`dsf-cli`** (`cli/`) — the factory CLI. Owns: `cli` (the `dsf new` factory CLI)
  and `instance` (provisioning). Depends on **`dsf-core` only** — provably
  agent-free, the literal realization of "extract the CLI from the agents."
- **`dsf-control-center`** (`control-center/`) — the governance web UI. Owns:
  `control_center`. Depends on `dsf-core` only (learning is in core).

`agents` stays a set of independently-deployable containers (thin ASGI entrypoints +
per-agent Dockerfiles) per ADR 0001 §1 — the container boundary provides portability;
the package boundary now provides ownership.

### Two forced changes

The split forces exactly two small, behaviour-preserving changes:

1. **Operator CLI moves packages.** `dsf.cli.control` → `dsf.runtime.control`
   (feature-council); the `dsfctl` console script repoints. The factory CLI stays at
   `dsf.cli.factory`. A single `dsf.*` subpackage cannot span two distributions, so
   `dsf.cli` belongs solely to `dsf-cli`. This is the only real import-path churn —
   one module + the script entry + a couple of test references.
2. **Control-center owns its serve command.** Drop `dsfctl control-center`; the
   control-center package exposes its own entrypoint
   (`dsf-control-center → dsf.control_center.app:main`, i.e.
   `uvicorn dsf.control_center.app:app`). This removes the only feature-council →
   control-center reference (today a lazy uvicorn import string) and keeps the layering
   acyclic. Refines ADR 0003, which had parked control-center serving under `dsfctl`.

## Approach

### Why a uv workspace + namespace packages

- **Self-contained apps at the repo root** match the owner's mental model: each
  member is its own directory with its own `pyproject.toml`, `src/dsf/...` tree,
  `tests/`, and (where applicable) `Dockerfile`.
- **No version-pin friction.** Apps depend on `dsf-core` via
  `[tool.uv.sources] dsf-core = { workspace = true }` resolved against **one shared
  `uv.lock`** — sidestepping the exact cross-package versioning pain ADR 0001 warned
  against while still giving real, enforced boundaries.
- **Near-zero import churn.** PEP 420 namespace packages (no top-level
  `dsf/__init__.py`) keep every existing `dsf.*` import path identical. Only the
  *packaging* changes — which distribution ships a given `dsf.*` subpackage — so the
  migration is moving directories + writing per-package manifests, not rewriting
  thousands of imports.

Alternatives considered and rejected: a single wheel with internal layers
(no independent build/deploy of apps) and separately-versioned packages
(reintroduces ADR 0001's version friction).

### Boundary enforcement (anti-drift)

Per-package dependencies already make illegal imports impossible (e.g. `dsf-cli` does
not depend on `dsf-feature-council`, so it cannot import it). On top of that, an
**import-linter** contract in CI codifies the layered architecture (`core` imports no
app; apps do not import each other) so the boundary cannot silently rot — the
enforcement ADR 0001's single package never had.

### Packaging & dependencies

Each member's `pyproject.toml` declares only the third-party deps its modules import:

- **dsf-core:** `pydantic`, `jsonschema`, `httpx`; `[optional-dependencies] azure` =
  `azure-appconfiguration`, `azure-cosmos`, `azure-identity`, `openai` (the Azure data
  adapters live in core); optional OpenTelemetry for the tracer.
- **dsf-feature-council:** `mcp` (agents), `fastapi`, `uvicorn` (agent A2A servers and
  serve commands).
- **dsf-cli:** `jinja2` (provisioning render templates).
- **dsf-control-center:** `fastapi`, `uvicorn`, `jinja2` (web UI).

Console scripts: `dsf → dsf.cli.factory:main` (dsf-cli) · `dsfctl →
dsf.runtime.control:main` (dsf-feature-council) · `dsf-control-center →
dsf.control_center.app:main` (dsf-control-center).

Shared `ruff` and `pytest` configuration stays at the workspace root so
`uv run ruff check .` and `uv run pytest -q` each remain a single command over the
whole workspace.

## Migration — incremental strangler

Each step is one PR; the suite stays green between PRs (namespace packages keep imports
stable, so only packaging moves).

1. **PR1 — Stand up `core/`.** Create the uv workspace; move the ten core subpackages
   into `core/src/dsf/...`; drop the top-level `dsf/__init__.py` (→ namespace package).
   Everything else stays in a temporary shrinking legacy member that depends on
   `dsf-core`. Author **ADR 0010**.
2. **PR2 — Peel `feature-council/`.** Move `agents`, `council`, `orchestrator`,
   `triggers`, `evals`, `runtime`, and the operator CLI; rename
   `dsf.cli.control → dsf.runtime.control` and repoint `dsfctl` (+ test refs) in the
   same PR.
3. **PR3 — Peel `cli/`.** Move the factory CLI + `instance`; confirm it resolves with
   **only** `dsf-core` (the agent-free proof).
4. **PR4 — Peel `control-center/`.** Move it; add its own serve entrypoint; drop
   `dsfctl control-center`; delete the now-empty legacy member. Add the import-linter
   CI contract.

## Testing

Validation gate run at **every** step (no step may regress it):

- `uv run ruff check .` — clean.
- `uv run pytest -q` — all current tests green (349 at design time); tests move to
  per-package `tests/` (each app independently testable) while the root config still
  collects all four so one command runs the whole suite.
- `uv run python -m dsf.evals.runner --gate` — PASSED (evals in feature-council).
- Offline dry-run runs the full 7-station council line.
- `dsf new --write-plan` dry-run still registers + routes a demo product.
- Each member `uv build`s independently.
- After PR4: the import-linter contract passes (no illegal cross-app imports), and
  `dsf-cli`'s resolved dependency set contains no agent/runtime package.

## Success criteria

- Four self-contained app directories at the repo root, each independently
  buildable/testable, each with a clear single purpose.
- Enforced acyclic `core ◀ apps` layering (per-package deps + import-linter).
- `/cli` is provably agent-free.
- Zero behaviour change; full suite + eval gate green throughout the migration.

## Out of scope

- Behavioural changes to any subsystem — this is a structural move only.
- Splitting the repository into multiple git repos (polyrepo) — the workspace stays a
  single repo.
- Re-homing the Azure data adapters or changing the `azure` extra contents beyond the
  package it attaches to.
