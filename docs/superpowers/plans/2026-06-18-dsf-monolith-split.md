# Split the `dsf` monolith into a uv workspace — Implementation Plan (#26)

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Convert the single `src/dsf` package into a uv workspace of self-contained members at the repo root (`core/`, `feature-council/`, `cli/`, `control-center/`) sharing one `dsf.*` namespace, starting with standing up the workspace and extracting `dsf-core`.

**Architecture:** PEP 420 namespace packages — no top-level `dsf/__init__.py`; each member is a hatchling distribution shipping a disjoint set of `dsf.*` subpackages and depending on `dsf-core` via a uv workspace source resolved against one shared `uv.lock`. The existing 349-test suite + eval gate is the regression safety net: this is a behaviour-preserving move, so every task ends by running the full gate and expecting it green. The migration is an incremental strangler — this plan fully details **Phase 1 (PR1: workspace + `dsf-core`)**; later phases are a roadmap (their exact `git mv` lists depend on the post-PR1 tree) and each gets its own detailed plan when reached.

**Tech Stack:** Python 3.12, uv workspaces, hatchling, pytest (asyncio_mode=auto), ruff.

**Spec:** `docs/superpowers/specs/2026-06-18-dsf-monolith-split-design.md`

**Pre-validated mechanic:** A throwaway two-member spike confirmed a hatchling member can import across the shared `dsf.` namespace from another member resolved via `[tool.uv.sources] dsf-core = { workspace = true }`, that `dsf` is a true PEP 420 namespace package (`__file__ is None`, multi-entry `__path__`), and that each member builds independently with no top-level `dsf/__init__.py` in the wheel.

---

## Phase 1 — Stand up the workspace and extract `dsf-core` (PR1)

**Branch:** `refactor/monolith-split` (the design spec already committed here).

**End state:** Root becomes a uv workspace whose members are the legacy `dsf` package (shrinking) and the new `dsf-core`. The ten core subpackages/modules move into `core/src/dsf/`. The suite stays green; nothing else moves; tests stay at the repo root.

**Core set moved in this phase** (into `core/src/dsf/`): `contracts/`, `ports/`, `config/`, `memory/`, `model/`, `observability/`, `a2a/`, `learning/`, `container.py`, `github_client.py`.

### Task 1: Confirm the green baseline

**Files:** none (verification only).

- [ ] **Step 1: Confirm branch + clean tree**

Run: `git -C /home/jbergfeld/vcs/dark-software-factory status -sb && git branch --show-current`
Expected: branch `refactor/monolith-split`; only the already-committed spec, no uncommitted changes.

- [ ] **Step 2: Run the full gate to record the baseline**

Run: `uv run ruff check . && uv run pytest -q && uv run python -m dsf.evals.runner --gate`
Expected: `All checks passed!`; `349 passed`; `GATE PASSED`.

### Task 2: Create the `core/` member and its `pyproject.toml`

**Files:**
- Create: `core/pyproject.toml`

- [ ] **Step 1: Create the core package manifest**

Create `core/pyproject.toml` with exactly:

```toml
[project]
name = "dsf-core"
version = "0.1.0"
description = "Dark Software Factory — shared core (contracts, ports, config, memory, model, observability, a2a, container, learning)"
requires-python = ">=3.12"
dependencies = [
    "pydantic>=2.6",
    "httpx>=0.27",
    "fastapi>=0.110",
]

[project.optional-dependencies]
azure = [
    "azure-appconfiguration>=1.5",
    "azure-cosmos>=4.5",
    "azure-identity>=1.15",
    "openai>=1.30",
]

[build-system]
requires = ["hatchling"]
build-backend = "hatchling.build"

[tool.hatch.build.targets.wheel]
packages = ["src/dsf"]
```

Note: `jsonschema` is intentionally omitted — it is imported nowhere in `src/` or `tests/` (dead dependency). `uvicorn`/`mcp` are not core (they belong to the app members in later phases).

### Task 3: Move the ten core subpackages/modules into `core/src/dsf/`

**Files:**
- Move: `src/dsf/{contracts,ports,config,memory,model,observability,a2a,learning}/` → `core/src/dsf/`
- Move: `src/dsf/container.py`, `src/dsf/github_client.py` → `core/src/dsf/`

- [ ] **Step 1: Create the core source tree root**

Run:
```bash
cd /home/jbergfeld/vcs/dark-software-factory
mkdir -p core/src/dsf
```

- [ ] **Step 2: Move the eight core subpackages with git mv**

Run:
```bash
cd /home/jbergfeld/vcs/dark-software-factory
for d in contracts ports config memory model observability a2a learning; do
  git mv "src/dsf/$d" "core/src/dsf/$d"
done
```

- [ ] **Step 3: Move the two core top-level modules**

Run:
```bash
cd /home/jbergfeld/vcs/dark-software-factory
git mv src/dsf/container.py core/src/dsf/container.py
git mv src/dsf/github_client.py core/src/dsf/github_client.py
```

- [ ] **Step 4: Remove the top-level package marker to make `dsf` a namespace package**

The current `src/dsf/__init__.py` contains only a docstring and an unused `__version__` (grep confirms nothing reads `dsf.__version__`). Both the legacy root member and `dsf-core` must contribute to one PEP 420 `dsf` namespace, so neither may carry `dsf/__init__.py`.

Run:
```bash
cd /home/jbergfeld/vcs/dark-software-factory
git rm src/dsf/__init__.py
```

Do **not** create `core/src/dsf/__init__.py` (the `git mv` did not produce one; the namespace root must stay marker-free).

- [ ] **Step 5: Verify the split is structurally correct**

Run:
```bash
cd /home/jbergfeld/vcs/dark-software-factory
test ! -e src/dsf/__init__.py && test ! -e core/src/dsf/__init__.py && echo "namespace OK"
ls core/src/dsf
ls src/dsf
```
Expected: `namespace OK`; `core/src/dsf` lists the 8 dirs + `container.py` + `github_client.py`; `src/dsf` lists the remaining nine (`agents cli control_center council evals instance orchestrator runtime triggers`) and no `__init__.py`.

### Task 4: Convert the root `pyproject.toml` into the workspace root

**Files:**
- Modify: `pyproject.toml`

- [ ] **Step 1: Rewrite the root manifest**

Replace the entire contents of `pyproject.toml` with exactly:

```toml
[project]
name = "dsf"
version = "0.1.0"
description = "Dark Software Factory — feature-intake line (legacy umbrella; being split into workspace members under #26)"
requires-python = ">=3.12"
dependencies = [
    "dsf-core",
    "pydantic>=2.6",
    "fastapi>=0.110",
    "uvicorn>=0.29",
    "httpx>=0.27",
    "jinja2>=3.1",
    "mcp>=1.2",
]

[project.scripts]
dsf = "dsf.cli.factory:main"
dsfctl = "dsf.cli.control:main"

[build-system]
requires = ["hatchling"]
build-backend = "hatchling.build"

[tool.hatch.build.targets.wheel]
packages = ["src/dsf"]

[tool.uv.workspace]
members = ["core"]

[tool.uv.sources]
dsf-core = { workspace = true }

[dependency-groups]
dev = [
    "pytest>=8.0",
    "pytest-asyncio>=0.23",
    "ruff>=0.4",
]

[tool.ruff]
line-length = 100
target-version = "py312"

[tool.ruff.lint]
select = ["E", "F", "I", "UP", "B"]

[tool.pytest.ini_options]
asyncio_mode = "auto"
testpaths = ["tests"]
pythonpath = ["src"]
```

Changes vs. the old root: dropped the dead `jsonschema` dep; removed the `[project.optional-dependencies]` table (the `azure` extra moved to `dsf-core`; `dev` moved to `[dependency-groups]`); added the workspace + source tables and `dsf-core` as a dependency. Scripts/ruff/pytest config unchanged. `pythonpath`/`testpaths` keep the root tests working during the strangler.

- [ ] **Step 2: Sync the workspace into a single environment**

Run: `cd /home/jbergfeld/vcs/dark-software-factory && uv sync`
Expected: resolves and installs both members (`dsf` and `dsf-core`) editable into `.venv` plus the `dev` group; exit 0.

- [ ] **Step 3: Verify the cross-namespace import resolves**

Run: `uv run python -c "import dsf.container, dsf.orchestrator, dsf.contracts; import dsf; print('ns entries:', len(list(dsf.__path__)), '| file:', dsf.__file__)"`
Expected: prints `ns entries: 2 | file: None` (legacy root + core both contribute to the `dsf` namespace), no ImportError.

### Task 5: Point the install tooling at the workspace

**Files:**
- Modify: `Makefile:3-5`
- Modify: `.github/workflows/ci.yml:18-21`

- [ ] **Step 1: Update the Makefile `install` target**

In `Makefile`, replace:

```makefile
install:
	uv venv --python 3.12
	uv pip install -e ".[dev]"
```

with:

```makefile
install:
	uv sync
```

(Leave `test`, `lint`, `fmt`, `dryrun`, `evals`, `new-demo` unchanged — they already use `uv run`.)

- [ ] **Step 2: Update the CI install steps**

In `.github/workflows/ci.yml`, replace the two steps:

```yaml
      - name: Set up Python
        run: uv venv --python 3.12
      - name: Install
        run: uv pip install -e ".[dev]"
```

with the single step:

```yaml
      - name: Install
        run: uv sync
```

(Leave the `Lint`, `Test`, and `Evals gate` steps unchanged.)

- [ ] **Step 3: Verify the gate is green on the workspace**

Run: `cd /home/jbergfeld/vcs/dark-software-factory && uv run ruff check . && uv run pytest -q && uv run python -m dsf.evals.runner --gate`
Expected: `All checks passed!`; `349 passed`; `GATE PASSED`.

- [ ] **Step 4: Verify `dsf-core` builds independently**

Run: `cd /home/jbergfeld/vcs/dark-software-factory && uv build --package dsf-core 2>&1 | tail -1 && python3 -c "import zipfile,glob; w=sorted(glob.glob('dist/dsf_core-*.whl'))[-1]; names=zipfile.ZipFile(w).namelist(); assert 'dsf/__init__.py' not in names, 'namespace violated'; print('built namespace wheel:', w)"`
Expected: `Successfully built .../dsf_core-0.1.0-py3-none-any.whl`; `built namespace wheel: ...`. Then clean up: `rm -rf dist`.

- [ ] **Step 5: Verify the factory dry-run still works end-to-end**

Run: `cd /home/jbergfeld/vcs/dark-software-factory && uv run dsf new --product demo --owner acme --name-prefix demo --write-plan 2>&1 | tail -5`
Expected: prints the provisioning plan including `register_product` and routes the demo product; exit 0. Clean up any written manifest if one was created outside a tmp dir.

### Task 6: Record the decision (ADR 0010) and supersede ADR 0001 §1

**Files:**
- Create: `docs/adr/0010-uv-workspace-monorepo.md`
- Modify: `docs/adr/0001-architecture-decisions.md` (§1)

- [ ] **Step 1: Write ADR 0010**

Create `docs/adr/0010-uv-workspace-monorepo.md` with:

```markdown
# ADR 0010 — uv workspace of self-contained members; `dsf.*` namespace packages

Status: Accepted · Date: 2026-06-18 · Supersedes ADR 0001 §1 ("single Python
package"); refines ADR 0003 (the operator CLI module path and control-center
serving move during the split). Design: docs/superpowers/specs/2026-06-18-dsf-monolith-split-design.md

## Context

Everything lived in one `src/dsf/**` package — factory CLI, control-center web app,
feature-council runtime, and instance-provisioning tooling — with no enforced
boundary between them. ADR 0001 §1 chose a single package to avoid cross-package
versioning friction; the cost was that ownership and layering were implicit and
unenforceable.

## Decision

Restructure the repository into a **uv workspace** of four self-contained members at
the repo root — `core` (`dsf-core`), `feature-council` (`dsf-feature-council`),
`cli` (`dsf-cli`), `control-center` (`dsf-control-center`) — sharing one `dsf.`
import namespace via **PEP 420 namespace packages** (no top-level `dsf/__init__.py`)
and one shared `uv.lock`. Apps depend on `dsf-core` via
`[tool.uv.sources] dsf-core = { workspace = true }`.

- The shared lockfile keeps a single resolved dependency set, neutralising the
  version-pin friction ADR 0001 §1 worried about while still giving real, enforced
  boundaries.
- Namespace packages keep every existing `dsf.*` import path identical, so the
  migration is packaging + directory moves, not a rewrite.
- Boundaries are enforced by per-member dependencies (an app cannot import a package
  it does not depend on) plus an import-linter contract in CI (added when the apps are
  peeled): `core` imports no app; apps do not import each other.

## Consequences

- The agents remain independently-deployable containers (ADR 0001 §1's portability
  goal) — the container boundary provides portability, the package boundary now
  provides ownership.
- `dsf-cli` (the factory CLI + provisioning) depends on `dsf-core` only and is
  provably agent-free.
- The operator CLI moves `dsf.cli.control → dsf.runtime.control` and the
  control-center gains its own serve entrypoint (refines ADR 0003), landing in the
  feature-council and control-center phases respectively.
- Migration is an incremental strangler kept green at every step; `dsf-core` is
  extracted first.
```

- [ ] **Step 2: Add a supersede note to ADR 0001 §1**

In `docs/adr/0001-architecture-decisions.md`, immediately after the §1 "Why" paragraph (the paragraph ending "...provides the portability, not a package boundary."), insert:

```markdown

> **Update (ADR 0010):** The "single Python package" decision is superseded by
> [ADR 0010](0010-uv-workspace-monorepo.md): the project is now a uv **workspace** of
> self-contained members (`dsf-core`, `dsf-feature-council`, `dsf-cli`,
> `dsf-control-center`) sharing one `dsf.` namespace and one lockfile. The per-agent
> container entrypoints and the ports posture are unaffected; the package boundary now
> additionally encodes ownership, enforced by per-member deps + an import-linter.
```

- [ ] **Step 3: Verify docs are coherent (no broken gate)**

Run: `cd /home/jbergfeld/vcs/dark-software-factory && uv run ruff check . && uv run pytest -q`
Expected: `All checks passed!`; `349 passed` (docs-only changes do not affect tests).

### Task 7: Commit Phase 1

- [ ] **Step 1: Stage and commit**

Run:
```bash
cd /home/jbergfeld/vcs/dark-software-factory
git add -A
git commit -m "refactor(#26): stand up uv workspace and extract dsf-core

Convert root into a uv workspace (members: legacy dsf + dsf-core). Move the ten
core subpackages/modules (contracts, ports, config, memory, model, observability,
a2a, learning, container.py, github_client.py) into core/src/dsf/ and make dsf a
PEP 420 namespace package (drop src/dsf/__init__.py). Apps will depend on dsf-core
via the workspace source + one shared uv.lock. Drop the dead jsonschema dep; move
the azure extra to dsf-core; move dev deps to a dependency-group; point Makefile +
CI at 'uv sync'. ADR 0010 records the decision and supersedes ADR 0001 §1. Suite
green (349) + eval gate PASSED throughout.

Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
git log -1 --format='%h %s'
```
Expected: a new commit hash + the subject line.

- [ ] **Step 2: Push and open the PR**

Run:
```bash
cd /home/jbergfeld/vcs/dark-software-factory
git push -u origin refactor/monolith-split
gh pr create --base main --title "refactor(#26): stand up uv workspace + extract dsf-core (Phase 1)" \
  --body "Phase 1 of the monolith split (spec: docs/superpowers/specs/2026-06-18-dsf-monolith-split-design.md). Stands up the uv workspace and extracts dsf-core behind a shared dsf.* namespace; suite green (349) + eval gate PASSED. Part of #26."
```
Expected: PR URL printed; CI green on the PR.

---

## Future phases (each gets its own detailed plan when reached)

These are sequenced after Phase 1 lands green on `main`. Their exact `git mv` lists and
per-member dep sets are written against the **post-Phase-1 tree**, so they are scoped
here as roadmap, not executable steps. Each phase is one PR, kept green via the full
gate.

### Phase 2 — Peel `feature-council/` (PR2)
- New member `feature-council/` (`dsf-feature-council`), deps: `dsf-core`, `mcp`,
  `fastapi`, `uvicorn`. Move `agents`, `council`, `orchestrator`, `triggers`, `evals`,
  `runtime` from `src/dsf/` → `feature-council/src/dsf/`.
- Rename the operator CLI `dsf.cli.control → dsf.runtime.control`; repoint the `dsfctl`
  console script (now declared by `dsf-feature-council`) and the handful of test
  references in the same PR.
- Add `feature-council` to `[tool.uv.workspace] members`. Gate green (349).

### Phase 3 — Peel `cli/` (PR3)
- New member `cli/` (`dsf-cli`), deps: `dsf-core`, `jinja2`. Move `cli` (factory) +
  `instance` from `src/dsf/` → `cli/src/dsf/`. Declare the `dsf` console script here.
- Assert the agent-free proof: `uv tree --package dsf-cli` contains no
  agent/runtime package. Gate green (349).

### Phase 4 — Peel `control-center/` (PR4)
- New member `control-center/` (`dsf-control-center`), deps: `dsf-core`, `fastapi`,
  `uvicorn`, `jinja2`. Move `control_center` → `control-center/src/dsf/`.
- Add a `main()` serve entrypoint to `dsf.control_center.app` and declare a
  `dsf-control-center` console script; drop the `dsfctl control-center` subcommand.
- The legacy root `src/dsf/` is now empty: remove the root `[project]`/`[build-system]`
  tables (root becomes a virtual workspace root), drop `pythonpath`/`testpaths` once
  obsolete. Add the **import-linter** dev dependency + a CI step with a layered
  contract (`core` imports no app; apps do not import each other). Gate green (349).

### Phase 5 — Per-package test reorganisation (PR5)
- Move each area's tests into its member's `tests/`. Resolve the shared-helper coupling
  surfaced during planning: `tests/support/source_double.py` is used by both `a2a`
  (core) and `agents` (feature-council), and `tests/council/conftest.py` helpers are
  imported via the `tests.council.conftest` path. Relocate shared doubles to a single
  importable home and switch pytest to `--import-mode=importlib` +
  `consider_namespace_packages=true` to avoid the `tests` package-name collision across
  members. Root config still collects all members so one `uv run pytest -q` runs the
  whole suite. Gate green.

---

## Self-review notes

- **Spec coverage:** Phase 1 implements the workspace + `dsf-core` + namespace +
  shared-lock + enforcement-foundation + ADR 0010 from the spec; Phases 2–5 cover the
  remaining members, the two forced changes (operator-CLI move, control-center serve),
  the import-linter, and the per-package test move. No spec requirement is unscoped.
- **Placeholders:** Phase 1 contains complete file contents and exact commands.
  Phases 2–5 are explicitly roadmap (detailed plans authored when reached against the
  real tree) — not placeholder steps within an executable task.
- **Consistency:** member/distribution names (`dsf-core`/`dsf-feature-council`/
  `dsf-cli`/`dsf-control-center`), the `dsf.runtime.control` operator-CLI path, and the
  per-member dep sets (core = pydantic/httpx/fastapi + azure extra; feature-council adds
  mcp/uvicorn; cli adds jinja2; control-center adds fastapi/uvicorn/jinja2) match the
  spec and the import analysis.
