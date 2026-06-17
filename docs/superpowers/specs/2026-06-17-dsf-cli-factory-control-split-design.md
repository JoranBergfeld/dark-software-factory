# Carve the CLI apart: `dsf` (factory) vs `dsfctl` (instance control) — Design

Status: Approved · Date: 2026-06-17 · Cross-cutting refactor (charter roadmap row
"CLI / runtime split")

## 1. Context

`src/dsf/cli.py` is a single argparse module that conflates two unrelated concerns:

- **Feature-council runtime operations** — `run`, `sweep`, `serve-orchestrator`,
  `serve-agent`, `control-center`. These *operate the feature council itself* (the
  thing that gets deployed into each product instance), plus a global `--mode`
  flag (`local`/`gh`/`azure`).
- **Factory / instance provisioning** — `new`. This *stamps out a new isolated
  product factory from the template* and drives `dsf.instance.*`.

Having both behind one `python -m dsf.cli` entry point is the source of confusion:
"the CLI you use to create an instance" is tangled with "the source of the feature
council you operate." This refactor separates them into two clearly-named tools.

The current entry point has **no console script**; it is invoked as
`python -m dsf.cli <cmd>` from the Dockerfile, Makefile, and docs. An SP1-era plan
note said "no console-script entry point — keep it that way"; this design
deliberately supersedes that note (recorded in ADR 0003).

## 2. Goals & non-goals

**Goals**

- Two distinct, well-named CLIs, each with one clear purpose:
  - **`dsf`** — the *factory* CLI: create/manage product instances from the
    template. Today: `dsf new`. Future (SP7): `dsf status|upgrade|destroy`.
  - **`dsfctl`** — the *instance control* CLI (kubectl-style): operate a running
    instance's feature-council runtime. `dsfctl run|sweep|serve-orchestrator|
    serve-agent|control-center`, with the global `--mode` flag.
- Stay inside the single `dsf` Python package (ADR 0001), with two console-script
  entry points.
- Full migration: update the Dockerfile, Makefile, tests, and living docs to the
  new commands. No backwards-compatibility shim — `python -m dsf.cli` is removed so
  the old tangled entry point cannot linger.
- Behaviour-preserving: every existing command keeps its exact flags and effects;
  only the *entry point it lives under* changes.

**Non-goals**

- No argument-shape changes. `dsf new` keeps `--product`, `--owner`, `--name-prefix`
  as today (product stays a flag, not a positional).
- No change to provisioning or runtime logic — this is purely a CLI carve-out.
- No rewrite of historical specs/plans under `docs/superpowers/`. Those are
  point-in-time records (like ADR 0001); only living user docs are updated.
- No new SP7 lifecycle subcommands yet — `dsf` ships with `new` only; the others
  are noted as the future home, not implemented.

## 3. Decision

Split `src/dsf/cli.py` into a `src/dsf/cli/` package with two entry-point modules
and two console scripts:

| Tool     | Module             | Purpose                              | Subcommands (today) |
|----------|--------------------|--------------------------------------|---------------------|
| `dsf`    | `dsf.cli.factory`  | create/manage instances from template| `new`               |
| `dsfctl` | `dsf.cli.control`  | operate a running instance's runtime | `run`, `sweep`, `serve-orchestrator`, `serve-agent`, `control-center` |

The global `--mode` flag moves to `dsfctl` only (the factory CLI does not use it).

## 4. Detailed design

### 4.1 Module layout

Convert the module `src/dsf/cli.py` into a package:

```
src/dsf/cli/
  __init__.py    # docstring only; no re-exports (avoids an ambiguous `main`)
  control.py     # dsfctl
  factory.py     # dsf
```

- `control.py` owns: `_print_run_summary`, `_get_services`, `_AGENT_MODULES`, the
  runtime handlers `_cmd_run`, `_cmd_sweep`, `_cmd_serve_orchestrator`,
  `_cmd_serve_agent`, `_cmd_control_center`, a `build_parser()` that adds the global
  `--mode` flag and the five runtime subcommands, `main(argv=None) -> int`, and a
  `if __name__ == "__main__": raise SystemExit(main())` guard.
- `factory.py` owns: `_print_plan`, `_cmd_new`, a `build_parser()` with the single
  `new` subcommand (all current `--product/--owner/--repo/--visibility/
  --runtime-target/--name-prefix/--environment/--location/--workload-principal-id/
  --execute/--write-plan/--config-root` options unchanged), `main(argv=None) -> int`,
  and a `__main__` guard.
- No shared `_common.py`: after the split the two modules share no code
  (`--mode`/run-summary are control-only; plan printing is factory-only). Preserve
  the existing lazy in-function imports (e.g. `from dsf.orchestrator.conveyor import
  run_line`) so parse-time stays light.

### 4.2 Console scripts and `python -m`

Add to `pyproject.toml`:

```toml
[project.scripts]
dsf = "dsf.cli.factory:main"
dsfctl = "dsf.cli.control:main"
```

Console scripts wrap as `sys.exit(main())`, so the existing `main() -> int` exit-code
contract is preserved. Both modules also keep a `__main__` guard, so
`python -m dsf.cli.factory` and `python -m dsf.cli.control` work without relying on
the console scripts being on `PATH` (useful in minimal containers and `uv run`).

### 4.3 Command mapping (old → new)

| Old (`python -m dsf.cli …`)              | New                                   |
|------------------------------------------|---------------------------------------|
| `run --dry-run --signal …`               | `dsfctl run --dry-run --signal …`     |
| `sweep`                                  | `dsfctl sweep`                        |
| `--mode azure serve-orchestrator`        | `dsfctl --mode azure serve-orchestrator` |
| `serve-agent --kind sentry --port 8080`  | `dsfctl serve-agent --kind sentry …`  |
| `control-center --port 8081`             | `dsfctl control-center --port 8081`   |
| `new --product … --owner … --name-prefix …` | `dsf new --product … --owner … --name-prefix …` |

### 4.4 Entry-point migration (no shim)

Delete `src/dsf/cli.py`. Update every caller:

- **`src/dsf/runtime/Dockerfile`** — `CMD ["dsfctl", "--mode", "azure",
  "serve-orchestrator"]` (the image already `pip install`s the package, so
  `/usr/local/bin/dsfctl` is on `PATH`). Update the accompanying comment.
- **`Makefile`** — `dryrun:` → `uv run dsfctl run --dry-run --signal
  tests/fixtures/sample_signal.json`; `new-demo:` → `uv run dsf new --product demo
  --owner your-org --name-prefix demo`.
- **Tests** — establish `tests/cli/` (mirrors `src/dsf/cli/`):
  - `tests/cli/__init__.py` (new).
  - Move `tests/instance/test_cli_new.py` → `tests/cli/test_factory.py`; change
    imports to `from dsf.cli.factory import build_parser, main`.
  - Move the `test_cli_*` functions out of `tests/test_container.py` (lines ~115-167)
    into `tests/cli/test_control.py`; change imports to `from dsf.cli.control import
    build_parser, main`. Leave the container-only tests (`build_services`,
    `AzureRuntimeSettings`, `Services`) in `test_container.py` and drop its now-unused
    `from dsf.cli import …` import.
  - `tests/e2e/test_dry_run_line.py` — `from dsf.cli.control import main`.
  - `tests/runtime/test_runtime_image.py` — assert the CMD contains
    `'CMD ["dsfctl", "--mode", "azure", "serve-orchestrator"]'`.
  - **New separation test** (in `tests/cli/test_control.py` and
    `tests/cli/test_factory.py`): the factory parser rejects a runtime command
    (`build_parser().parse_args(["run"])` raises `SystemExit`) and the control parser
    rejects `new` — proving the surfaces are actually disjoint.

### 4.5 Docs cleanup

Living, user-facing docs only:

- **`README.md`** — replace `python -m dsf.cli control-center` with
  `dsfctl control-center`; fix the broken Quickstart `dsf new` example (it currently
  reads `dsf new microbi --name-prefix microbi`, which fails because `--product` and
  `--owner` are required) to `dsf new --product microbi --owner your-org
  --name-prefix microbi`; add a one-line note that there are two CLIs — `dsf` to
  create instances, `dsfctl` to control one.
- **`docs/RUNBOOK.md`** — rewrite every `python -m dsf.cli <runtime-cmd>` to
  `dsfctl <runtime-cmd>` (including `dsfctl --mode azure serve-orchestrator`), and
  every `python -m dsf.cli new …` to `dsf new …`.
- **Charter** (`docs/superpowers/specs/2026-06-17-dark-software-factory-template-
  charter-design.md`) — mark the "CLI / runtime split" cross-cutting roadmap row as
  done, naming the `dsf`/`dsfctl` outcome.
- **`infra/README.md`** — only if a `dsf.cli` invocation is present (none known); no
  change otherwise.
- Historical specs/plans under `docs/superpowers/` are **not** edited.

### 4.6 ADR 0003

Add `docs/adr/0003-two-clis-factory-and-instance-control.md` recording: the two-CLI
split and naming (`dsf` = factory/template, `dsfctl` = instance control); that both
live in the single `dsf` package via `[project.scripts]` (consistent with ADR 0001);
and that this supersedes the SP1 "no console-script entry point" note. Consequences:
the Dockerfile/Makefile/docs invoke the new commands; `python -m dsf.cli` is gone.

## 5. Testing strategy

- TDD per the repo norm: write/adjust the failing test, then move the code.
- The suite count stays ~equal (moved tests) plus the two small separation tests.
- `tests/runtime/test_runtime_image.py` pins the new CMD; `tests/cli/test_control.py`
  and `tests/cli/test_factory.py` pin each parser's subcommands and their disjointness.
- Full verification gate: `uv run ruff check .` clean; `uv run pytest -q` green;
  `uv run python -m dsf.evals.runner --gate` PASSED (unaffected, but run to be sure).

## 6. Risks & edge cases

- **Stale `python -m dsf.cli` references** anywhere uncaught would break. Mitigation:
  a repo-wide grep for `dsf.cli` (excluding historical `docs/superpowers/plans|specs`
  archives) is part of the plan's final step.
- **Console scripts require an install.** Tests invoke `main()` directly (no PATH
  dependency); the Dockerfile/Makefile run after install, so `dsf`/`dsfctl` resolve.
- **`uv run dsfctl`/`uv run dsf`** resolve the project venv console scripts after
  `uv pip install -e ".[dev]"` — verified path for the Makefile targets.

## 7. Verification

`uv run ruff check .` && `uv run pytest -q` && `uv run python -m dsf.evals.runner
--gate`, plus a manual smoke of each new entry point:
`uv run dsf new --product demo --owner your-org --name-prefix demo` (prints the plan)
and `uv run dsfctl run --dry-run --signal tests/fixtures/sample_signal.json`.
