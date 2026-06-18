# Reframe fakes as honest local implementations — Implementation Plan (#27)

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans (inline)
> or superpowers:subagent-driven-development to implement this plan task-by-task. Steps
> use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Delete `src/dsf/fakes/`, reframing its load-bearing classes as honest,
domain-co-located implementations and moving the one genuine test-double to `tests/`, with
no behavior change.

**Architecture:** This is a behavior-preserving rename + relocate refactor. Each task moves
one concept to its honest home, updates every reference (src + tests), and must leave
`uv run pytest -q` green before committing. The eval gate (`uv run python -m
dsf.evals.runner --gate`) is the determinism guard run after any model/fixture change.

**Tech Stack:** Python 3.12, uv, pytest, ruff. Commands: `uv run ruff check .`,
`uv run pytest -q`, `uv run python -m dsf.evals.runner --gate`, `make dryrun`.

**Discipline note:** Behavior is unchanged, so the safety net is the *existing* suite staying
green, not new red→green tests. Where a class moves, port the body verbatim and change only
the class name, module docstring, and any name-derived string. Add a small characterization
test only where a new module/path needs coverage.

**Spec:** `docs/superpowers/specs/2026-06-18-remove-fakes-honest-local-implementations-design.md`

---

### Task 1: `NoOpTracer` (lowest-risk, self-contained)

**Files:**
- Modify: `src/dsf/observability/tracing.py` (already imports `FakeTracer`)
- Delete: `src/dsf/fakes/tracer.py`
- Modify: `src/dsf/fakes/__init__.py` (drop the tracer export)
- Modify: `src/dsf/container.py` (import `NoOpTracer` from observability)
- Test: `tests/observability/test_tracing.py`

- [ ] **Step 1: Move + rename the class.** Copy the `FakeTracer` body from
  `src/dsf/fakes/tracer.py` into `src/dsf/observability/tracing.py` as
  `class NoOpTracer` (no-op span context manager recording `self.spans`). Update
  `build_tracer` (`tracing.py:33-51`) to return `NoOpTracer()` for `"local"` and as the OTel
  fallback. Remove the `from dsf.fakes... FakeTracer` import there.

- [ ] **Step 2: Update consumers.** In `src/dsf/container.py`, replace the `FakeTracer`
  import (line 16) and its three uses (lines 107, 118; line 131 uses `build_tracer`) with
  `NoOpTracer` imported from `dsf.observability.tracing`. Delete `src/dsf/fakes/tracer.py`
  and remove `FakeTracer` from `src/dsf/fakes/__init__.py`.

- [ ] **Step 3: Update tests.** In `tests/observability/test_tracing.py` (and any other
  importer found by `grep -rln FakeTracer tests/`), import `NoOpTracer` from
  `dsf.observability.tracing` and rename usages.

- [ ] **Step 4: Verify.** Run `uv run pytest tests/observability tests/test_container.py -q`
  → PASS. Then `uv run ruff check src/dsf/observability/tracing.py src/dsf/container.py` → clean.

- [ ] **Step 5: Commit.**
```bash
git add -A && git commit -m "refactor(observability): FakeTracer -> NoOpTracer in tracing.py (#27)"
```

---

### Task 2: `RecordingGitHubClient`

**Files:**
- Modify: `src/dsf/github_client.py` (add the class beside `RealGitHubClient`)
- Delete: `src/dsf/fakes/github.py`
- Modify: `src/dsf/fakes/__init__.py`, `src/dsf/container.py`
- Test: `tests/test_container.py`, `tests/test_integration_issues.py`, others via grep

- [ ] **Step 1: Move + rename.** Copy `FakeGitHubClient` (`src/dsf/fakes/github.py:6-24`)
  into `src/dsf/github_client.py` as `class RecordingGitHubClient` — records `create_issue`
  calls in `self.calls`, returns `local://issue/<n>`. Add a one-line docstring framing it as
  the in-memory recording client for local mode.

- [ ] **Step 2: Update consumers.** In `container.py`, import `RecordingGitHubClient` from
  `dsf.github_client` and use it in the `local` branch (line 106). Delete
  `src/dsf/fakes/github.py`; remove the export from `fakes/__init__.py`.

- [ ] **Step 3: Update tests.** For each file in `grep -rln "FakeGitHubClient" tests/`,
  import `RecordingGitHubClient` from `dsf.github_client` and rename usages.

- [ ] **Step 4: Verify.** `uv run pytest tests/test_container.py tests/test_integration_issues.py -q`
  → PASS; `uv run ruff check src/dsf/github_client.py src/dsf/container.py` → clean.

- [ ] **Step 5: Commit.**
```bash
git add -A && git commit -m "refactor(github): FakeGitHubClient -> RecordingGitHubClient (#27)"
```

---

### Task 3: `InMemoryConfigStore` + `load_defaults`

**Files:**
- Create: `src/dsf/config/store.py`
- Delete: `src/dsf/fakes/config_store.py`
- Modify: `src/dsf/fakes/__init__.py`, `src/dsf/container.py`, `src/dsf/control_center/app.py`,
  `src/dsf/cli/control.py`, `src/dsf/config/flags.py` (if it references the fake)
- Test: `tests/config/test_flags.py`, `tests/config/test_registry.py`, others via grep

- [ ] **Step 1: Move + rename.** Move `src/dsf/fakes/config_store.py` content to
  `src/dsf/config/store.py`: `class InMemoryConfigStore` (was `FakeConfigStore`) with its
  `from_defaults()`/`get_value()`/override logic verbatim, plus the `load_defaults()` and
  `_repo_root()` helpers. Keep the `ConfigStore` port import.

- [ ] **Step 2: Update consumers.** Replace imports/uses in `container.py` (lines 12, 105,
  116, 129), and every hit from `grep -rln "FakeConfigStore\|from dsf.fakes import.*ConfigStore\|load_defaults" src/`
  (notably `control_center/app.py`, `cli/control.py`). Delete `src/dsf/fakes/config_store.py`;
  remove exports from `fakes/__init__.py`.

- [ ] **Step 3: Update tests.** For each `grep -rln "FakeConfigStore\|fakes.config_store\|load_defaults" tests/`,
  import from `dsf.config.store` and rename.

- [ ] **Step 4: Verify.** `uv run pytest tests/config tests/control_center tests/test_container.py -q`
  → PASS; ruff clean on touched files.

- [ ] **Step 5: Commit.**
```bash
git add -A && git commit -m "refactor(config): FakeConfigStore -> InMemoryConfigStore in config/store.py (#27)"
```

---

### Task 4: `InMemoryMemoryStore`

**Files:**
- Create: `src/dsf/memory/store.py`
- Delete: `src/dsf/fakes/memory.py`
- Modify: `src/dsf/fakes/__init__.py`, `src/dsf/container.py`, `src/dsf/triggers/debounce.py`
- Test: `tests/` via grep (memory, triggers, orchestrator, learning)

- [ ] **Step 1: Move + rename.** Move `src/dsf/fakes/memory.py` content to
  `src/dsf/memory/store.py`: `class InMemoryMemoryStore` (was `FakeMemoryStore`) with the
  working/long-term/lesson stores, TTL, eviction, and `_tokens`/`_overlap` similarity verbatim.

- [ ] **Step 2: Update consumers.** Replace imports/uses in `container.py` (lines 14, 104,
  115, 128) and every `grep -rln "FakeMemoryStore\|fakes.memory" src/` hit (notably
  `triggers/debounce.py`). Delete `src/dsf/fakes/memory.py`; remove export from `fakes/__init__.py`.

- [ ] **Step 3: Update tests.** Rename across `grep -rln "FakeMemoryStore\|fakes.memory" tests/`.

- [ ] **Step 4: Verify.** `uv run pytest tests/memory tests/triggers tests/orchestrator tests/learning tests/test_container.py -q`
  → PASS; ruff clean on touched files.

- [ ] **Step 5: Commit.**
```bash
git add -A && git commit -m "refactor(memory): FakeMemoryStore -> InMemoryMemoryStore in memory/store.py (#27)"
```

---

### Task 5: `DeterministicModelClient` (eval-gate sensitive)

**Files:**
- Create: `src/dsf/model/__init__.py`, `src/dsf/model/client.py`
- Delete: `src/dsf/fakes/model.py`
- Modify: `src/dsf/fakes/__init__.py`, `src/dsf/container.py`, `src/dsf/council/synthesizer.py`
- Test: Create `tests/model/__init__.py`, `tests/model/test_client.py`; update via grep

- [ ] **Step 1: Move + rename.** Move `src/dsf/fakes/model.py` to `src/dsf/model/client.py`:
  `class DeterministicModelClient` (was `FakeModelClient`) with the deterministic
  prompt-handler registry + call recording verbatim. Change ONLY the fallback string: replace
  `f"[fake-model] {prompt}"` with a neutral deterministic form, e.g.
  `f"[deterministic] {prompt}"`. Add `src/dsf/model/__init__.py` exporting
  `DeterministicModelClient`.

- [ ] **Step 2: Update consumers.** Replace imports/uses in `container.py` (lines 15, 103,
  114, 127) and `grep -rln "FakeModelClient\|fakes.model" src/` (notably
  `council/synthesizer.py`). Delete `src/dsf/fakes/model.py`; remove export from `fakes/__init__.py`.

- [ ] **Step 3: Update tests.** Move the model-specific cases from `tests/fakes/test_fakes.py`
  into `tests/model/test_client.py`; import `DeterministicModelClient` from `dsf.model`.
  Rename across any other `grep -rln "FakeModelClient" tests/`. If a test asserts the old
  `[fake-model]` prefix, update it to `[deterministic]`.

- [ ] **Step 4: Verify (determinism guard).**
```bash
uv run pytest tests/model tests/council tests/test_container.py -q   # PASS
uv run python -m dsf.evals.runner --gate                              # GATE PASSED, all metrics 1.000
```
  If the gate regresses, diff the model output vs the prior handler logic — only the fallback
  prefix should differ; ensure no eval case depends on the literal `[fake-model]` text.

- [ ] **Step 5: Commit.**
```bash
git add -A && git commit -m "refactor(model): FakeModelClient -> DeterministicModelClient in dsf.model (#27)"
```

---

### Task 6: Per-agent `*FixtureBackend`

**Files:**
- Modify: `src/dsf/agents/{sentry,grafana,webiq,foundryiq,tickets}/backend.py`,
  `.../main.py`, `.../__init__.py`
- Test: `tests/agents/{sentry,grafana,webiq,foundryiq,tickets}/...`

- [ ] **Step 1: Rename in place.** In each agent's `backend.py`, rename the offline backend
  class `*FakeBackend` → `*FixtureBackend` (e.g. `SentryFakeBackend` → `SentryFixtureBackend`).
  Update the docstring that says "Shaped like `dsf.fakes.source.FakeSourceBackend`" to
  reference the fixture-replay behavior without the `dsf.fakes` path.

- [ ] **Step 2: Update selection + exports.** In each `main.py`, update the `else` branch
  (`backend = *FixtureBackend()`); in each `__init__.py`, update the export name in
  `__all__` and imports.

- [ ] **Step 3: Update tests.** Rename across `grep -rln "FakeBackend" tests/agents/` for all
  five agents.

- [ ] **Step 4: Verify.**
```bash
uv run pytest tests/agents -q                 # PASS
uv run python -m dsf.evals.runner --gate      # GATE PASSED (fixtures feed golden cases)
```

- [ ] **Step 5: Commit.**
```bash
git add -A && git commit -m "refactor(agents): *FakeBackend -> *FixtureBackend across all source agents (#27)"
```

---

### Task 7: Move the generic source double to `tests/`; delete `fakes/` packages

**Files:**
- Create: `tests/support/__init__.py`, `tests/support/source_double.py`
- Delete: `src/dsf/fakes/source.py`, `src/dsf/fakes/__init__.py` (now empty), the
  `src/dsf/fakes/` dir, `tests/fakes/` dir
- Modify: `tests/agents/test_base.py`, `tests/a2a/test_client.py`, `tests/a2a/test_server.py`

- [ ] **Step 1: Relocate the double.** Move `FakeSourceBackend` (`src/dsf/fakes/source.py`)
  to `tests/support/source_double.py` as `class RecordingSourceBackend` (records `gather`
  calls, returns a fixed evidence list). Add `tests/support/__init__.py`.

- [ ] **Step 2: Update test importers.** In `tests/agents/test_base.py`,
  `tests/a2a/test_client.py`, `tests/a2a/test_server.py`, import `RecordingSourceBackend`
  from `tests.support.source_double` and rename usages.

- [ ] **Step 3: Delete the packages.** Confirm `src/dsf/fakes/__init__.py` has no remaining
  exports, then `git rm -r src/dsf/fakes` and `git rm -r tests/fakes` (its remaining cases,
  if any, were redistributed in Tasks 1–5; move any leftovers to the matching `tests/<domain>`
  first).

- [ ] **Step 4: Verify.**
```bash
test ! -d src/dsf/fakes && echo "fakes pkg gone"
uv run pytest -q                              # FULL suite PASS
```

- [ ] **Step 5: Commit.**
```bash
git add -A && git commit -m "refactor(tests): move generic source double to tests/support; delete src/dsf/fakes (#27)"
```

---

### Task 8: Cleanse residuals, ADR 0005, final verification

**Files:**
- Modify: `src/dsf/evals/runner.py` (rename the `fake evidence id` sentinel)
- Create: `docs/adr/0005-honest-local-implementations.md`
- Modify: `docs/adr/0001-architecture-decisions.md` (superseded-in-part note)
- Modify: any remaining `src/` docstring/comment hits from the final grep

- [ ] **Step 1: Cleanse evals + residuals.** In `src/dsf/evals/runner.py:124-153`, rename the
  `_seed_ungrounded_proposal` "fake evidence id" sentinel/variable to a neutral term
  (`synthetic_evidence_id` / "ungrounded"). Then run
  `grep -rni "\bfake" src/ --include="*.py" | grep -v __pycache__` and fix every remaining
  hit (docstrings/comments) to the new vocabulary.

- [ ] **Step 2: ADR 0005.** Create `docs/adr/0005-honest-local-implementations.md` (Status:
  Accepted; supersedes ADR 0001's in-memory-fakes posture). Record: ports + honest,
  domain-co-located local implementations (`InMemory*` / `NoOp*` / `Recording*` /
  `*Fixture*` / `Deterministic*`); pure test-doubles live under `tests/`; **never create
  `Fake`/stub/mock vocabulary or auto-generated stand-ins in `src/` without explicit owner
  approval after asking.** In `docs/adr/0001-architecture-decisions.md`, add a note that its
  fakes posture is superseded by ADR 0005 (other decisions stand).

- [ ] **Step 3: Final verification.**
```bash
grep -rni "\bfake" src/ --include="*.py" | grep -v __pycache__   # (empty)
uv run ruff check .                                              # All checks passed!
uv run pytest -q                                                 # full suite PASS
uv run python -m dsf.evals.runner --gate                         # GATE PASSED
make dryrun                                                      # runs the line offline
```

- [ ] **Step 4: Commit.**
```bash
git add -A && git commit -m "refactor(evals,docs): cleanse residual fake refs; ADR 0005 honest local impls (#27)"
```

---

## Self-Review

- **Spec coverage:** Decision A (reframe) → all tasks; B (rename map) → Tasks 1–7;
  C (DeterministicModelClient) → Task 5; D (container/modes) → Tasks 1–5 touch `container.py`;
  E (test strategy) → Tasks 1–7 + Task 7 relocation; F (cleanse residuals) → Task 8 Step 1;
  G (ADR 0005 + rule) → Task 8 Step 2. All Done-when items appear in Task 8 Step 3.
- **Placeholder scan:** none — every task lists exact files, the move/rename, the consumer
  updates, and exact verify/commit commands.
- **Type consistency:** new names are used identically across tasks
  (`InMemoryConfigStore`, `InMemoryMemoryStore`, `NoOpTracer`, `RecordingGitHubClient`,
  `DeterministicModelClient`, `*FixtureBackend`, `RecordingSourceBackend`). Ports are
  unchanged, so all implementations keep satisfying `dsf.ports`.
- **Ordering:** least-coupled first; model (Task 5) and fixtures (Task 6) precede package
  deletion (Task 7) and run the eval gate; final grep/ruff/suite/gate/dryrun in Task 8.
