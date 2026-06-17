# ADR 0003 — Two CLIs: `dsf` (factory) and `dsfctl` (instance control)

Status: Accepted · Date: 2026-06-17 · Supersedes the SP1 note "no console-script
entry point — keep it that way" (docs/superpowers/plans/2026-06-17-dsf-new-greenfield-instance.md)

## Context

`src/dsf/cli.py` had grown to conflate two unrelated concerns behind one
`python -m dsf.cli` entry point: operating a running instance's feature-council
runtime (`run`/`sweep`/`serve-orchestrator`/`serve-agent`/`control-center`) and
provisioning a brand-new product factory from the template (`new`). "The CLI you run
to create an instance" was tangled with "the source of the feature council you
operate," which was confusing.

## Decision

1. **Two console scripts, one package.** Per ADR 0001 the project stays a single
   `dsf` Python package. `src/dsf/cli.py` becomes a `src/dsf/cli/` package exposing
   two entry points via `[project.scripts]`:
   - **`dsf`** → `dsf.cli.factory:main` — the *factory* CLI: create/manage product
     instances from the template (`dsf new`; future SP7 `status`/`upgrade`/`destroy`).
   - **`dsfctl`** → `dsf.cli.control:main` — the *instance control* CLI (kubectl-style):
     operate a running instance's feature-council runtime, including the global
     `--mode` flag (`local`/`gh`/`azure`).

2. **Full migration, no shim.** `python -m dsf.cli` is removed; the Dockerfile,
   Makefile, tests, and living docs invoke `dsf`/`dsfctl`. Removing the old entry
   point prevents the tangled surface from lingering.

3. **Behaviour-preserving.** Each subcommand keeps its exact flags and effects; only
   the entry point it lives under changes.

## Consequences

- The runtime image runs `CMD ["dsfctl", "--mode", "azure", "serve-orchestrator"]`.
- Each module keeps a `__main__` guard, so `python -m dsf.cli.control` /
  `python -m dsf.cli.factory` also work without the console scripts being on `PATH`.
- New instance-lifecycle verbs have an obvious home (`dsf`), and runtime ops have an
  obvious home (`dsfctl`), so neither CLI accretes unrelated commands.
- This is the cross-cutting "CLI / runtime split" from the template charter roadmap.
