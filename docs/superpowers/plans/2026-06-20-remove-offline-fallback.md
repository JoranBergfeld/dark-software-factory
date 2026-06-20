# Remove offline / fallback / fake from `src/` — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: superpowers:subagent-driven-development. Steps use checkbox (`- [ ]`) syntax. This is a deletion/relocation refactor: most "tests" are "the existing suite stays green + grep shows the symbol is gone from `src/`". Run the gate after every task.

**Goal:** `src/` contains only real implementations; every deterministic double lives in `testing/dsf_testing/`; the system always executes (dry-run is a user-only flag); DSF is pull-only.

**Architecture:** Move the honest doubles to `dsf_testing`, add `dsf_testing.build_test_services()` so tests stop calling `build_services("local")`. Collapse `build_services` to a single real, fail-loud wiring with no `mode`. Drop the push/signal-buffer path. Decouple the council from the deterministic echo. Tickets agent removed (tracked in issue #60).

**Tech Stack:** Python 3.12, uv workspace, pytest (importlib mode), import-linter, ruff.

## Global Constraints

- `uv` only; run everything via `uv run`. Never bare `python`/`pip`/`pytest`.
- ruff: line length 100, target py312, rules `E,F,I,UP,B`.
- All I/O-bearing port methods are `async`.
- No `Fake*` names; doubles are `InMemory*` / `Deterministic*` / `Recording*` / `NoOp*`.
- No deferred functionality in `src/` — defer via GitHub issues instead.
- Import boundaries (`make lint-imports`) and `make test` must pass after every task.
- The gate after every task: `uv run ruff check . && uv run lint-imports && uv run pytest -q`.

## Execution order (each is a green commit)

1. Task 1 — Drop the push path (signals/ingestion/inbox)
2. Task 2 — Stand up `dsf_testing` doubles + `build_test_services`
3. Task 3 — Collapse `build_services` to real-only, no mode, fail loud
4. Task 4 — Decouple the council from the deterministic echo
5. Task 5 — Sweep tests onto `build_test_services`; move doubles out of `src/`
6. Task 6 — Source agents: real config injection; remove tickets agent
7. Task 7 — A2A auth always enforced
8. Task 8 — Dry-run is user-only; `dsf new` executes by default
9. Task 9 — CI / Makefile / docs / ADR / CLAUDE.md

---

### Task 1: Drop the push path (pull-only)

**Files:**
- Delete: `core/src/dsf/signals/` (`__init__.py`, `buffer.py`), `core/tests/signals/`
- Delete: `feature-council/src/dsf/triggers/ingestion.py` and its test
- Modify: `core/src/dsf/ports/__init__.py` — remove the `SignalBuffer` Protocol + its `__all__` entry
- Modify: `core/src/dsf/container.py` — remove the `signals` field from `Services` and all `InMemorySignalBuffer` wiring (the SignalBuffer import too)
- Modify: `feature-council/src/dsf/triggers/app.py` — remove `/ingest` and `/file` endpoints + their dry-run two-step
- Modify: `feature-council/src/dsf/triggers/scheduler.py` — remove `drain_signals` and the buffer-drain half of the orchestrator tick; keep `sweep`
- Modify: `feature-council/src/dsf/runtime/control.py` — `serve-orchestrator` drains nothing now; it just sweeps sources
- Update/trim tests under `feature-council/tests/triggers/` for the removed surface

**Steps:**
- [ ] Remove `SignalBuffer` from `ports/__init__.py` and the `signals` package; grep `grep -rn "SignalBuffer\|InMemorySignalBuffer\|drain_signals\|enqueue" core feature-council` returns only the dropped tests (then delete those).
- [ ] Remove the `signals` field from `Services` and all signal wiring in `container.py`.
- [ ] Delete `ingestion.py`; remove `/ingest` + `/file` from `app.py`; remove `drain_signals` from `scheduler.py`. Keep `sweep` and `dsfctl run --signal`.
- [ ] Gate: `uv run ruff check . && uv run lint-imports && uv run pytest -q`. Expected: green (after trimming the now-dead tests).
- [ ] Commit: `feat: drop push/signal-buffer path — DSF is pull-only (sweep)`.

### Task 2: Stand up `dsf_testing` doubles + `build_test_services`

**Files:**
- Create: `testing/dsf_testing/model.py` (`DeterministicModelClient`, `ECHO_PREFIX`)
- Create: `testing/dsf_testing/memory.py` (`InMemoryMemoryStore`)
- Create: `testing/dsf_testing/config.py` (`InMemoryConfigStore`)
- Create: `testing/dsf_testing/github.py` (`RecordingGitHubClient`)
- Create: `testing/dsf_testing/tracing.py` (`NoOpTracer`)
- Create: `testing/dsf_testing/services.py` (`build_test_services`)
- Modify: `testing/dsf_testing/__init__.py` — re-export all of the above

**Interfaces:**
- Produces: `build_test_services(*, model=None, memory=None, config=None, github=None, tracer=None, product=None) -> dsf.container.Services` — wires the doubles, allowing per-field overrides. Defaults: `DeterministicModelClient()`, `InMemoryMemoryStore()`, `InMemoryConfigStore.from_defaults()`, `RecordingGitHubClient()`, `NoOpTracer()`.

**Steps:**
- [ ] Copy each double's source into the matching `dsf_testing` module (do not delete from `src/` yet — that happens in Task 5 after importers move). Keep class names identical.
- [ ] Write `build_test_services` against the `Services` dataclass from `dsf.container` (note: `Services` no longer has `mode`/`signals` after Tasks 1+3; for now match the current dataclass and fix in Task 3).
- [ ] Re-export from `__init__.py`.
- [ ] Gate. Expected: green (pure addition).
- [ ] Commit: `test: add dsf_testing doubles + build_test_services`.

### Task 3: Collapse `build_services` to real-only, no mode, fail loud

**Files:**
- Modify: `core/src/dsf/container.py`
- Modify: `core/src/dsf/observability/tracing.py` — `build_tracer` no longer takes a mode; it builds the real OTel tracer and raises if OTel is missing (no NoOp fallback in `src/`)
- Modify: `core/tests/test_container.py`, `core/tests/observability/test_tracing.py`

**Transformation:**
- `Services` dataclass: drop `mode` (and `signals`, already dropped in Task 1). Keep `model/memory/config/github/tracer/product/azure`.
- `build_services(*, env=None) -> Services`: no `mode` param. Resolve `AzureRuntimeSettings.from_env`. **Every** endpoint required — raise `ValueError` listing the missing var if `appconfig_endpoint`, `cosmos_endpoint`, `openai_endpoint`, or `openai_deployment` is blank. Wire `AppConfigStore`, `CosmosMemoryStore`, `AzureOpenAIModelClient`, `AzureOpenAIEmbeddingClient`, `RealGitHubClient`, real tracer. No `else InMemory*` branches. Delete the `local`/`gh` branches and the `NotImplementedError`.
- `NoOpTracer` moves to `dsf_testing` in Task 5; until then leave it in `tracing.py` but stop using it in `build_tracer`.

**Steps:**
- [ ] Rewrite `container.py` per the transformation.
- [ ] Update `test_container.py` to test the real-only contract: missing endpoint → raises; with all env set, the right adapter types are wired (these may need adapter constructors that tolerate fake endpoints without network — assert on type, do not call network methods).
- [ ] Update `build_test_services` (Task 2) to match the trimmed `Services`.
- [ ] Gate. Expected: green.
- [ ] Commit: `feat: build_services is real-only and fail-loud; remove mode`.

### Task 4: Decouple the council from the deterministic echo

**Files:**
- Modify: `feature-council/src/dsf/council/deliberation.py`, `jury.py`, `synthesizer.py`
- Modify council tests: `feature-council/tests/council/test_deliberation.py`, `test_jury.py`, `test_synthesizer.py`, and any of `test_decision.py`/`test_critics.py`/e2e that lean on echo→fallback

**Transformation (remove `from dsf.model.client import ECHO_PREFIX` everywhere):**
- `deliberation.py`: `_parse_position` — adopt a `LensPosition`; any other result → the deterministic critic fallback (drop the `ECHO_PREFIX` string check and the free-text-rationale capture). Remove the `except Exception` silent degrade in `_lens_position` so a real model error propagates (the conveyor records it as `ERROR`). The deterministic critic stays as the legitimate baseline when the model returns no structured position.
- `jury.py`: give jurors a structured schema (`class JurorDecision(BaseModel): go: bool; rationale: str = ""`). `convene_jury` calls `complete(..., schema=JurorDecision)`. Parse the structured result; a non-`JurorDecision` result → `fallback_go`. Delete `_parse_vote` keyword parsing and `_vote_text`/`ECHO_PREFIX`.
- `synthesizer.py`: give the prose step a structured schema (`class ProposalProse(BaseModel): title: str`). Use the model title only when a `ProposalProse` comes back; otherwise the deterministic `fallback_title`. Delete `_prose_or_fallback`'s `[deterministic]` check.
- Council tests: stop relying on the bare echo. Construct a `DeterministicModelClient` from `dsf_testing` and `register(tag, handler)` returning the structured `LensPosition`/`JurorDecision`/`ProposalProse` for the cases that exercise the model path; for the "no model" baseline, assert the critic/fallback values directly.

**Steps:**
- [ ] Rework the three council modules to be structured-or-critic with no `ECHO_PREFIX` import.
- [ ] Update the council tests to register structured handlers (or assert the deterministic baseline).
- [ ] Gate. Expected: green. `grep -rn ECHO_PREFIX feature-council/src` returns nothing.
- [ ] Commit: `refactor(council): structured model output or critic baseline; drop echo coupling`.

### Task 5: Sweep tests onto `build_test_services`; remove doubles from `src/`

**Files:**
- Modify: ~40 test files that call `build_services("local")` (full list from `grep -rn 'build_services("local")' --include='*.py'`)
- Modify src entry points still defaulting to local services:
  - `feature-council/src/dsf/triggers/app.py:43` `_LOCAL_SERVICES = build_services("local")`
  - `feature-council/src/dsf/runtime/control.py` `--mode` plumbing
  - `control-center/src/dsf/control_center/app.py` local default + `DSF_MODE`
  - `feature-council/src/dsf/evals/runner.py` `services_factory=build_services` default
- Delete from `src/`: `DeterministicModelClient` (`core/src/dsf/model/client.py` + `__init__.py` export), `InMemoryMemoryStore` (`core/src/dsf/memory/store.py`; keep `Memory` and the shared base), `InMemoryConfigStore` (`core/src/dsf/config/store.py`; keep the shared `_BaseConfig`/`resolve_flag_key` used by `azure_store.py`), `RecordingGitHubClient` (`core/src/dsf/github_client.py`; keep `RealGitHubClient`), `NoOpTracer` (`core/src/dsf/observability/tracing.py`)

**Steps:**
- [ ] Sweep every test: `from dsf.container import build_services` + `build_services("local")` → `from dsf_testing import build_test_services` + `build_test_services()`. Where a test reached into a specific double (e.g. asserting on `RecordingGitHubClient.calls`), import that double from `dsf_testing` and pass it via `build_test_services(github=...)`. (Parallelizable across files — dispatch one subagent per test directory.)
- [ ] Rewire the four src entry points: `app.py`/`control_center` build real services lazily (a factory that calls `build_services()`), no `DSF_MODE`. `runtime/control.py` drops `--mode`. `evals/runner.py` — its default factory is removed (the eval gate goes in Task 9); the runner keeps a required injected factory for any retained unit test.
- [ ] Delete the five doubles from `src/`. Keep the real adapters and the shared helpers (`Memory`, config base, `RealGitHubClient`, OTel tracer).
- [ ] Gate. Expected: green. `grep -rn 'InMemory\|Deterministic\|Recording\|NoOpTracer' core/src feature-council/src cli/src control-center/src` returns only real-adapter docstring mentions (clean those too).
- [ ] Commit: `refactor: tests use build_test_services; doubles removed from src`.

### Task 6: Source agents — real config injection; remove tickets agent

**Files:**
- Modify each agent `main.py` (`webiq, azuremonitor, grafana, foundryiq, sentry, incidents`): drop `else InMemoryConfigStore.from_defaults()`. The agent requires an injected `ConfigStore`; the serve entrypoint passes `build_services().config`. Move any `*FixtureBackend` selection (the `local → fixture` branch) out — agents build the live backend and raise on missing env.
- Move `*FixtureBackend` classes to `testing/dsf_testing/` (or each agent's `tests/`).
- Delete: `feature-council/src/dsf/agents/tickets/` entirely.
- Modify: `feature-council/src/dsf/agents/registry.py` — remove tickets from `DEPLOYABLE_AGENTS`.
- Modify: `core/src/dsf/contracts/enums.py` — remove `SourceKind.TICKETS` only if nothing real references it; otherwise leave the enum value and note it unused.
- Update agent tests accordingly.

**Steps:**
- [ ] Rewire the six remaining agents to require injected config + live backend; relocate fixtures to the test side.
- [ ] Delete the tickets agent and deregister it (issue #60 already filed).
- [ ] Gate. Expected: green.
- [ ] Commit: `refactor(agents): live-only backends, injected config; remove tickets agent (#60)`.

### Task 7: A2A auth always enforced

**Files:**
- Modify: `core/src/dsf/a2a/auth.py` — remove the `mode`/`DSF_MODE`/`_LOCAL` no-op bypass. `build_bearer_dependency(expected_token)` raises at setup if the token is blank; the dependency always enforces. Drop `_resolve_effective_mode`.
- Update: `core/tests/a2a/` auth tests — blank token now raises everywhere; tests that needed open auth pass an explicit test token.

**Steps:**
- [ ] Simplify `auth.py` to always-enforce; blank token → `RuntimeError` at setup.
- [ ] Update auth tests.
- [ ] Gate. Expected: green.
- [ ] Commit: `feat(a2a): always enforce bearer auth (no local bypass)`.

### Task 8: Dry-run is user-only; `dsf new` executes by default

**Files:**
- Modify: `feature-council/src/dsf/config/flags.py` — remove `dry_run_global` and the global `dry_run` kill-switch reads
- Modify: `feature-council/src/dsf/orchestrator/stations/s7_filing.py` — `dry = run.dry_run` only (no global)
- Modify: `feature-council/src/dsf/runtime/control.py` — keep `dsfctl run --dry-run` (sets `run.dry_run = True`)
- Modify: `cli/src/dsf/cli/factory.py` + `cli/src/dsf/instance/provisioner.py` — flip to execute-by-default; add `--dry-run` for the what-if preview; remove `--execute`
- Update CLI + s7 tests

**Steps:**
- [ ] Remove the global kill-switch; `s7_filing` honors only `run.dry_run`.
- [ ] Flip `dsf new` to execute-by-default with `--dry-run` preview.
- [ ] Update tests.
- [ ] Gate. Expected: green.
- [ ] Commit: `feat: dry-run is user-invoked only; dsf new executes by default`.

### Task 9: CI / Makefile / docs / ADR / CLAUDE.md

**Files:**
- Modify: `.github/workflows/ci.yml` — drop the evals gate step; CI = ruff → lint-imports → pytest
- Modify: `Makefile` — remove `dryrun`, `evals`, `new-demo` targets (or repoint `dryrun` to a documented real-services command)
- Delete or quarantine: `feature-council/src/dsf/evals/` if nothing else uses it; otherwise keep the runner but remove the `--gate` CI usage
- Create: `docs/adr/00NN-real-only-src-no-offline.md` superseding ADR 0005; record real-only `src/`, doubles in `tests/`, no-mode wiring, pull-only scope, deferred-work-as-issues
- Modify: `docs/adr/0005-*.md` — mark superseded
- Modify: `CLAUDE.md` — drop all mode/`local`/`gh`/`azure` language and the "honest impls in src" guidance; state real-only + pull-only + no-deferred-code + doubles-in-tests rules
- Modify: `core/src/dsf/ports/__init__.py` docstring — drop the "offline runs" framing

**Steps:**
- [ ] Update CI, Makefile, ADRs, CLAUDE.md, ports docstring.
- [ ] Gate. Expected: green.
- [ ] Commit: `docs: real-only/pull-only ADR; drop eval gate and mode language`.

## Self-review notes

- Spec coverage: §1 doubles→Tasks 2/5; §2 no-mode fail-loud→Task 3; §3 pull-only→Task 1; §4 council→Task 4; §5 agents/tickets→Task 6; §6 dry-run→Task 8; §7 auth→Task 7; §8 CI/docs→Task 9. Covered.
- Risk: Task 4 (council) and Task 6 (agent config) are the deep ones. They are sequenced after the doubles exist in `dsf_testing` (Task 2) so tests can register handlers. If Task 4 destabilizes, it is the natural stopping point — leave the branch green at Task 3 and file a follow-up issue rather than ship a broken council.
- `AzureRuntimeSettings`/adapter constructors must build without live network so `test_container.py` can assert wiring by type. If an adapter constructor dials out, the test asserts the raise path instead and a follow-up issue tracks a lazy-connect refactor.
