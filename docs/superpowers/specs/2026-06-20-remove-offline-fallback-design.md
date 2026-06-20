# Remove offline / fallback / fake from `src/`

Date: 2026-06-20
Status: Draft (for review)
Supersedes: ADR 0005 (honest local implementations live in `src/`)
Related: GitHub issue #60 (deferred tickets-in-council agent)

## Goal

`src/` should contain only real implementations. No code that pretends to be a service,
and no code where the *system* decides not to execute. Every deterministic double moves to
the test side; tests inject them. Configuration that isn't real fails loud instead of
silently degrading to an in-memory stub.

This reverses ADR 0005's one controversial rule ("keep the honest doubles in `src/`") while
keeping its spirit: the doubles are still honest (no `Fake*` names), they just live in
`tests/` and `testing/dsf_testing/` now.

## Principles

1. **`src/` is real-only.** No `InMemory*` / `Deterministic*` / `Recording*` / `NoOp*`
   implementations, no fixture backends, no stubs in shipped code.
2. **Fail loud.** Missing endpoints, credentials, or model output that can't be parsed
   raise. The system never substitutes a canned value.
3. **The system always executes.** Dry-run is a user-invoked preview only (like
   `terraform plan` / `bicep what-if`), never a system default.
4. **No deferred functionality in code.** Out-of-scope work is removed and tracked as a
   GitHub issue, not parked as a stub. (See issue #60.)
5. **Pull, not push (for now).** DSF gets work by sweeping source agents on a schedule.
   The push/inbox model (durable queue) is explicitly future scope.

## Scope of changes

### 1. Move offline ports out of `src/`

Relocate to `testing/dsf_testing/`:

- `DeterministicModelClient` (`core/src/dsf/model/client.py`)
- `InMemoryMemoryStore` (`core/src/dsf/memory/store.py`)
- `InMemoryConfigStore` (`core/src/dsf/config/store.py`)
- `RecordingGitHubClient` (`core/src/dsf/github_client.py`)
- `NoOpTracer` (`core/src/dsf/observability/tracing.py`)

`src/` keeps only the real adapters: `AzureOpenAIModelClient`, `CosmosMemoryStore`,
`AppConfigStore`, `RealGitHubClient`, the OTel tracer.

Any `src/` code that currently constructs one of these (e.g. a debounce helper using
`InMemoryMemoryStore`) is rewired to take the real store via dependency injection.

### 2. `build_services()` — no mode, fail loud

- Remove the `mode` parameter entirely. There is one wiring: real services.
- Delete the `local` / `gh` / `azure` mode strings, the `DSF_MODE` env var, and every
  `--mode` flag (`dsfctl`, Control Center).
- Every endpoint/credential is required. If `appconfig_endpoint`, `cosmos_endpoint`,
  `openai_endpoint`, `openai_deployment`, or `DSF_PRODUCT` is unset, raise a clear error.
  Delete all `if endpoint: Real() else: InMemory()` fallbacks.

### 3. Drop the push path (pull-only)

Remove:

- The `SignalBuffer` port and `InMemorySignalBuffer` (`core/src/dsf/signals/`).
- `triggers/ingestion.py` (the webhook → buffer ingestion).
- The `/ingest` and `/file` HTTP endpoints and their dry-run two-step (`triggers/app.py`).
- `scheduler.drain_signals` and the buffer-drain half of `serve-orchestrator`.

Keep:

- `sweep` — the orchestrator polls source agents for evidence. This is the pull model and
  the system's only automated work source now.
- `dsfctl run --signal <json>` — a user-invoked manual run of a single signal through the
  line (ops/debug entry, not a subscription).

Service Bus / durable inbox is **not** built. There is no in-memory stand-in either.

### 4. Council — fail loud, no deterministic safety nets

Remove the degrade-to-deterministic paths now that `DeterministicModelClient` is gone from
`src/`:

- `deliberation.py` — the "lens model unavailable; used deterministic critic" fallback and
  echo-detection (`ECHO_PREFIX`) handling.
- `jury.py` — echo/unparseable → fallback verdict.
- `synthesizer.py` — `[deterministic]`-prefixed output → fallback prose.

If the model returns output that can't be parsed, the station raises (and the conveyor's
per-station handler records it as an `ERROR` terminal state, as it does today). A broken
model surfaces as an error, not a canned score.

### 5. Source agents — live only

- Each agent `main.py` loses its `local → *FixtureBackend` branch and always builds the
  live backend, raising on missing env (most already do).
- Fixture backends (`*FixtureBackend`) move to the test side.
- **Tickets agent removed entirely**: delete `agents/tickets/`, drop it from
  `agents/registry.py` `DEPLOYABLE_AGENTS`, remove `SourceKind.TICKETS` usage where it only
  existed to support the stub. The future "council agent that weighs ticket significance" is
  tracked in issue #60, not stubbed in code.

### 6. Dry-run — explicit user flag only

- Remove every system-initiated dry-run: `DEFAULT_DRY_RUN`, scheduler forcing
  `dry_run=True`, the global `dry_run` config kill-switch. (Most of this leaves with the
  push path in §3.)
- Keep `Run.dry_run`, reachable only via explicit user flags: `dsfctl run --dry-run` and
  `dsf new --dry-run`.
- `dsf new` flips from plan-by-default to **execute-by-default**; `--dry-run` produces the
  what-if preview. `--execute` is removed (executing is the default).
- `s7_filing` still honors `run.dry_run` (skip `create_issue`), but the only way that flag
  is set is an explicit user request.

### 7. A2A auth — always enforced

Remove the local-mode no-op bearer dependency. Bearer auth is always enforced (fail closed).
There is no offline mode in which auth is disabled.

### 8. CI / build / docs

- Drop the eval gate from CI. CI becomes ruff → lint-imports → pytest. A future cassette
  (record/replay) eval harness is noted as out of scope here.
- Remove `make dryrun` and `make new-demo` (both depended on the no-creds local mode).
- New ADR superseding 0005, documenting: doubles live in `tests/`/`testing/`, real-only
  `src/`, no-mode wiring, pull-only scope, deferred-work-as-issues policy.
- Update `CLAUDE.md` to drop all mode/`local`/`gh`/`azure` language and the "honest impls in
  src" guidance, and to state the real-only + pull-only + no-deferred-code rules.

## Testing strategy

- Tests construct `Services` by hand, injecting doubles from `dsf_testing` (now the home of
  `InMemoryMemoryStore`, `DeterministicModelClient`, etc.). No `build_services("local")`.
- The import-linter contracts are unaffected: `dsf_testing` is a top-level package outside
  the `dsf.*` namespace, so test code importing doubles doesn't cross a `dsf.*` boundary.
- `make test` (pytest) stays green; the eval gate is removed, not ported.

## Out of scope (tracked elsewhere)

- Tickets-in-council agent — issue #60.
- Durable push inbox / Azure Service Bus signal buffer — future, not stubbed.
- Cassette-based eval gate — future, noted in the new ADR.

## Risks / call-outs

- **`dsf new` execute-by-default** provisions real Azure + GitHub immediately. This is a
  deliberate posture change ("the user dictates"); `--dry-run` is the safety preview.
- **No durable inbox** means signals are not retained across restarts; acceptable under
  pull-only scope.
- Removing the eval gate reduces CI coverage until a cassette harness exists.
