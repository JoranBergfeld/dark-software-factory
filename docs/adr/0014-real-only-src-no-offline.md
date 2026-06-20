# ADR 0014: Real-only `src/`, doubles in `dsf_testing`, pull-only

- Status: Accepted
- Date: 2026-06-20
- Supersedes: ADR 0005 (honest local implementations in `src/`)
- Relates to: ADR 0011 (deliberative council)

## Context

ADR 0005 kept deterministic offline implementations in `src/` so the whole line
could run with no Azure subscription and no LLM (`DSF_MODE=local`). That was
handy, but it meant shipped code was carrying stand-ins that pretend to be
services: a `DeterministicModelClient` that echoes prompts, an
`InMemoryMemoryStore`, a `RecordingGitHubClient`, a `NoOpTracer`, an in-process
signal buffer, per-agent fixture backends, and a pile of "if the endpoint is
unset, fall back to the in-memory sibling" branches in the container.

The owner asked to get all of that out of `src/`. The objection is simple: as a
user you expect the thing to actually execute. A system that quietly runs on
stubs (or quietly declines to act) is lying about what it does. The honest
doubles are still useful, but they belong to the tests, not the product.

## Decision

- **`src/` ships only real implementations.** No `InMemory*` / `Deterministic*`
  / `Recording*` / `NoOp*` classes, no fixture backends, no stubs.
- **The doubles move to the `dsf_testing` package** (already on the pytest
  pythonpath). They keep ADR 0005's honest names. Tests build a service bundle
  with `dsf_testing.build_test_services()` instead of `build_services("local")`.
- **No `mode`.** `build_services()` takes no mode argument and wires only the
  real Azure adapters (App Configuration, Cosmos, Azure OpenAI, the OTel tracer,
  the real GitHub client). The `local` and `gh` modes are gone, along with
  `DSF_MODE` and every `--mode` flag.
- **Fail loud.** If a required endpoint or credential is missing,
  `build_services()` raises and names what is unset. It never substitutes a stub.
- **Pull, not push.** The signal buffer / webhook inbox is removed. DSF gets
  work by sweeping the source agents on a schedule. A durable push inbox (Azure
  Service Bus) is future scope, not stubbed.
- **Dry-run is user-only.** It is a preview flag the user passes (`dsfctl run
  --dry-run`, `dsf new --dry-run`), like `terraform plan`. The system never
  defaults to dry-run and there is no global kill switch. `dsf new` provisions
  for real by default.
- **No deferred functionality in code.** When something is out of scope we delete
  it and open a GitHub issue, rather than parking a stub. The tickets source
  agent was removed this way (issues #60, #61).

## Consequences

- There is no longer an offline way to run the whole line end to end. Running it
  for real needs Azure endpoints and credentials. Tests cover behavior by
  injecting the `dsf_testing` doubles directly.
- CI drops the eval gate (it ran the golden set through the conveyor on the
  deterministic model, which no longer lives in `src/`). CI is now ruff,
  lint-imports, pytest. Rebuilding the eval gate on recorded model responses
  (cassettes) is future work.
- A few things are deliberately left for follow-ups so the branch stays honest
  and green rather than half-done:
  - `InMemoryConfigStore` still lives in `src/` because the six source agents
    default to it and build their ASGI app at import time. Making the agents
    real-only (injected config, live backends, uvicorn `--factory`) is issue #61,
    which also carries the always-enforce A2A auth change.
  - The deferred tickets-aware council agent is issue #60.
- The council no longer detects the deterministic echo. Lenses and jurors use
  the model's structured output when it is there and fall back to their
  deterministic critic otherwise. The critics are real council logic, not a
  stub, so they stay.
