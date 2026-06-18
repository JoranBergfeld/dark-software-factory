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
