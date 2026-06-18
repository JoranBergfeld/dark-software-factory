# Reframe fake implementations as honest local implementations (#27)

- Status: Approved
- Date: 2026-06-18
- Issue: [#27](https://github.com/JoranBergfeld/dark-software-factory/issues/27)
- New ADR: 0005 (supersedes the "in-memory fakes for every port" posture of ADR 0001)

## Context

`src/dsf/fakes/` holds six `Fake*` classes wired by `build_services('local')` and used
pervasively (146 `fake` references across `src/`, ~23 test files). The owner never asked
for "fake" implementations and finds the vocabulary makes the code hard to read, maintain,
and move forward.

An inventory showed that **only one** of the six is a genuine test-double. The rest are
legitimate implementations that were merely *named* "Fake":

| Class | What it actually is |
|---|---|
| `FakeConfigStore` | Real in-memory + `config/defaults.json` flag store (default for the CLI **and** Control Center) |
| `FakeMemoryStore` | Real in-memory store with TTL + token-overlap similarity search |
| `FakeTracer` | Null-object no-op tracer (the real fallback when OpenTelemetry is absent) |
| `FakeGitHubClient` | Recording client — records intended `create_issue` calls, returns `local://issue/<n>`, files nothing |
| `*FakeBackend` (per agent) | Fixture-replay backends that really read JSON fixtures |
| `FakeModelClient` | **The only true test-double** — deterministic prompt→canned-response registry + echo fallback |

The offline-first posture the owner values (ADR 0001: 300+ tests, the eval gate, and
`make dryrun` all run with **no cloud and no LLM**) depends on these implementations
existing and being importable by `src/` (the eval runner and dry-run path live in `src/`).
Dry-run filing is gated by a **flag** (`s7_filing.py:36`: `run.dry_run or
dry_run_global(services.config)`), *not* by the GitHub client type — so the recording
client is a local-mode convenience, not the dry-run mechanism.

## Goal

Eliminate the "Fake" vocabulary and the `src/dsf/fakes/` package by **reframing** the
load-bearing implementations as honest, first-class, domain-co-located code, and **moving**
the one genuine test-double out to `tests/`. Preserve the offline/dry-run/eval posture
unchanged.

## Decisions

### A. Reframe, do not remove capability
`src/` keeps offline operation. Every implementation that runs the real pipeline offline is
reclassified as a legitimate implementation with an honest name, co-located with the domain
it implements. No behavior changes; this is a rename + relocate + reframe refactor.

### B. Rename + relocation map

| Old (`src/dsf/fakes/`) | New name | New home |
|---|---|---|
| `FakeConfigStore`, `load_defaults` | `InMemoryConfigStore`, `load_defaults` | `src/dsf/config/store.py` |
| `FakeMemoryStore` | `InMemoryMemoryStore` | `src/dsf/memory/store.py` |
| `FakeTracer` | `NoOpTracer` | `src/dsf/observability/tracing.py` |
| `FakeGitHubClient` | `RecordingGitHubClient` | `src/dsf/github_client.py` (beside `RealGitHubClient`) |
| `FakeModelClient` | `DeterministicModelClient` | `src/dsf/model/client.py` (new package `src/dsf/model/`) |
| `SentryFakeBackend` etc. (5 agents) | `SentryFixtureBackend` etc. | in place (`src/dsf/agents/<kind>/backend.py`) |
| `FakeSourceBackend` (generic) | `RecordingSourceBackend` | **`tests/support/source_double.py`** (test-only) |

Then delete the `src/dsf/fakes/` package entirely.

The agents already pick `is_live(resolve_mode(mode))` → live (MCP) vs offline backend; only
the offline class name changes (`*Fake*` → `*Fixture*`). `src/dsf/agents/mode.py` keeps its
`local` → fixture, else → live mapping.

### C. The offline model
`DeterministicModelClient` reuses the existing deterministic prompt-handler logic from
`FakeModelClient`, honestly named and positioned as **the offline model for local mode and
deterministic evals** — explicitly not an LLM and not a mock. The echo fallback is reframed
as a deterministic default response (drop the `"[fake-model] "` prefix; use a neutral
deterministic format). It lives in `src/` so the eval runner and `make dryrun` keep working
offline and deterministically.

### D. `container.py` + mode semantics
`build_services('local')` wires `InMemoryConfigStore.from_defaults()`,
`InMemoryMemoryStore()`, `RecordingGitHubClient()`, `DeterministicModelClient()`,
`NoOpTracer()`. `gh` and `azure` modes keep `InMemoryConfigStore` / `InMemoryMemoryStore` /
`DeterministicModelClient` as the **explicit offline seam** until SP3b swaps in real Cosmos /
App Configuration / LLM adapters. Docstrings change "fakes behind the seam" →
"in-memory/deterministic local implementations behind the seam." `azure` continues to use
`build_tracer('azure')` (OTel, degrading to `NoOpTracer`).

### E. Test strategy
- The ~23 test files that import from `dsf.fakes` or assert on the local bundle are
  re-pointed to the new names/locations.
- `tests/fakes/test_fakes.py` is split into per-domain tests that live beside the code they
  exercise: `tests/config/`, `tests/memory/`, `tests/observability/`, and a new
  `tests/model/`. The `tests/fakes/` directory is removed.
- The generic recording source double moves to `tests/support/source_double.py` as
  `RecordingSourceBackend`; consumers (`tests/agents/test_base.py`, the two `tests/a2a/`
  tests) import it from there. Agent backend docstrings stop referencing
  `dsf.fakes.source.FakeSourceBackend`.
- `src/` must not import from `tests/`.

### F. Cleanse residual references
Every remaining `src/` reference to the old names — imports, type hints, docstrings, and
comments in `council/synthesizer.py`, `triggers/debounce.py`, `triggers/app.py`,
`control_center/app.py`, `cli/control.py`, and the agent backend docstrings — is updated to
the new names. In `src/dsf/evals/runner.py`, the `_seed_ungrounded_proposal` "fake evidence
id" sentinel is renamed to a neutral term (e.g. `synthetic`/`ungrounded` evidence id) so the
word `fake` is gone from evals. This is required to satisfy the Done-when grep.

### G. ADR 0005 + the no-auto-fakes rule
Add `docs/adr/0005-honest-local-implementations.md` (Accepted, supersedes ADR 0001's
in-memory-fakes posture). It records: ports + honest, domain-co-located local
implementations (`InMemory*` / `NoOp*` / `Recording*` / `*Fixture*` / `Deterministic*`) for
offline operation; pure test-doubles live under `tests/`; **no `Fake`/stub/mock vocabulary
or auto-generated stand-ins in `src/` without explicit owner approval after asking**. Mark
ADR 0001 with a note that its fakes posture is superseded by ADR 0005 (its other decisions
stand).

## Done when

- `src/dsf/fakes/` no longer exists; `tests/fakes/` no longer exists.
- `grep -rni "\bfake" src/` returns nothing (the word is gone from source).
  Historical ADRs are exempt (ADR 0001's superseded-note may name the change).
- `src/dsf/model/` exists with `DeterministicModelClient`; `src/dsf/config/store.py`,
  `src/dsf/memory/store.py` exist with the `InMemory*` classes; `NoOpTracer` and
  `RecordingGitHubClient` live in `observability/tracing.py` and `github_client.py`.
- `uv run ruff check .` clean.
- `uv run pytest -q` green (count may shift as `tests/fakes/` is redistributed).
- `uv run python -m dsf.evals.runner --gate` PASSES (all aggregate metrics ≥ thresholds).
- `make dryrun` runs the full line offline and deterministically.
- ADR 0005 added; ADR 0001 annotated as superseded-in-part.

## Out of scope

- De-hardcoding the deployable-agent registry out of the CLI (#25).
- Splitting the monolith into standalone apps (#26).
- Real Azure data adapters (SP3b) — this spec only renames/relocates the offline seam; SP3b
  replaces it.
- Any behavior change to the pipeline, council, or eval logic.

## Risks

- **Wide blast radius** (container, ~23 test files, every agent, observability, evals,
  triggers, control-center, council). Mitigated by TDD task-by-task with frequent commits
  and the eval gate as a determinism guard after any model/fixture touch.
- **`DeterministicModelClient` output drift** could break the eval gate. Mitigation: keep the
  existing handler logic byte-for-byte except the renamed echo prefix; run the gate
  immediately after the model task.
- **Stale `.pyc`** under `__pycache__` may still match `grep fake`; exclude `__pycache__`
  and `/.git/` in verification.
