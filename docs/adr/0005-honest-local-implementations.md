# ADR 0005: Honest local implementations (no "fakes" in `src/`)

- Status: Superseded by ADR 0014
- Date: 2026-06-18
- Supersedes (in part): ADR 0001 §2 ("Ports + in-memory fakes")
- Superseded by: ADR 0014 (real-only `src/`, doubles in `dsf_testing`)

> **Superseded.** ADR 0014 keeps this ADR's naming rule (no `Fake*`; the doubles
> stay honest `InMemory*`/`Deterministic*`/`Recording*`/`NoOp*`) but moves them
> out of `src/` into the `dsf_testing` test package. `src/` now ships only real
> implementations, there is no in-process `local`/`gh` mode, and DSF is
> pull-only. See ADR 0014.

## Context

ADR 0001 §2 established `typing.Protocol` ports for every external dependency
with "deterministic in-memory **fakes**" in a dedicated `dsf.fakes` package.
Over time that framing spread through the codebase: `Fake*` class names, the
`dsf.fakes` package, per-agent `*FakeBackend` classes, and "fake" scattered
across docstrings and comments. The vocabulary obscured an important fact —
these are legitimate, deterministic **offline implementations** of the ports
(used for local development, CI, and the eval gate), not throwaway test
doubles. The owner never asked for "fakes"; the naming made `src/` harder to
read, maintain, and reason about.

## Decision

- **Keep the posture from ADR 0001 §2 unchanged.** Every external dependency
  hides behind a `typing.Protocol` port; the whole line runs end-to-end offline
  (`DSF_MODE=local`) with no Azure subscription and no LLM. Ports remain the
  seam where real Azure/MCP adapters swap in (`DSF_MODE=azure`).
- **Rename the implementations to honest, domain-co-located classes and delete
  the `dsf.fakes` package:**
  - `InMemoryConfigStore` (`dsf.config.store`)
  - `InMemoryMemoryStore` (`dsf.memory.store`)
  - `NoOpTracer` (`dsf.observability.tracing`)
  - `RecordingGitHubClient` (`dsf.github_client`)
  - `DeterministicModelClient` (`dsf.model.client`)
  - per-agent `*FixtureBackend` (`dsf.agents.<kind>.backend`)
- **Pure test-doubles live under `tests/`, never in `src/`.** The generic
  recording source backend now lives at `tests/support/source_double.py` as
  `RecordingSourceBackend`.
- **Naming convention going forward:** honest local implementations use the
  prefixes `InMemory*` / `NoOp*` / `Recording*` / `Deterministic*` / `*Fixture*`
  that describe *what they actually do*.
- **Policy:** never introduce `Fake`/stub/mock vocabulary or auto-generated
  stand-in implementations in `src/` **without explicit owner approval after
  asking.**

## Consequences

- `src/` contains no "fake" vocabulary. The change is behavior-preserving — the
  offline/dry-run posture and the eval gate are unchanged, with the existing
  test suite as the safety net.
- The `azure` mode keeps the `InMemory*` / `Deterministic*` implementations
  behind the ports until SP3b swaps in real Cosmos / App Configuration / LLM
  adapters. The seam (the ports) is unchanged.
- ADR 0001 §2's other decisions (ports + offline-by-default) stand; only the
  "in-memory fakes" naming and the `dsf.fakes` package are superseded.
