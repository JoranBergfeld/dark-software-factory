# SP3 — Feature-council runtime productionization (design)

> Sub-project SP3 of the DSF template+CLI charter. Brainstormed autonomously
> (owner reviewing async). Predecessors: SP1 (`dsf new` walking skeleton),
> SP2 (per-product Azure provisioning).

## 1. Context & current state

The factory's "feature council" is not a standalone service — it is the
`s3_synthesis` + `s5_council` stations inside the orchestrator **conveyor**
(`dsf.orchestrator.conveyor.run_line`). The orchestrator **worker** drives that
conveyor; today `dsf serve-orchestrator` runs a single in-process sweep
(`triggers.scheduler.run_sweep`) and notes "a real deployment would loop on a
queue".

Services are selected by **mode** in `dsf.container.build_services`:

- `local` — all in-memory fakes (default; deterministic, offline).
- `gh` — fakes + `RealGitHubClient` (shells out to `gh`).
- `azure` — **raises `NotImplementedError`** today.

SP2 made `dsf new --execute` provision a dedicated per-product Azure resource
group and a Bicep deployment, capturing outputs into the instance manifest
(`config/instances/<product>.json` → `manifest.azure.outputs`): `cosmosEndpoint`,
`appConfigEndpoint`, `keyVaultUri`, `appInsightsConnectionString`,
`eventGridTopicEndpoint`, `serviceBusNamespace`, `signalsQueueName`.

The provisioner plan still carries `deploy_council` (and `deploy_sre`) as
**deferred** stub steps. Per **ADR 0002**, the runtime is hosted in a homelab
(docker compose) and reaches Azure **backing services** outbound; Bicep deploys
**no compute**. `infra/compose.homelab.yml` already anticipates `DSF_MODE: azure`
with Azure endpoints wired in — but only for the Grafana *source agent*; there is
no orchestrator/council runtime image or compose yet. Dockerfiles exist only for
the five source agents.

`Run.product: str | None`, `Proposal.product`, and every config-flag accessor
(`critic_enabled`, `threshold`, …) already take an optional `product`, so
per-product scoping is a matter of *setting* `run.product`, not re-plumbing.

## 2. Goal

Make a **per-product council runtime deployable and product-scoped**, and make
`azure` mode a real, runnable mode:

1. `build_services('azure')` stops raising — it resolves validated Azure runtime
   settings from the environment and wires a usable `Services` bundle.
2. A deployed instance is **pinned to one product** (single-product scoping).
3. `dsf new` **un-defers `deploy_council`**: it renders a per-product
   orchestrator-worker runtime bundle (compose + env) from the instance
   manifest, and under `--execute` brings it up.

This yields a "council walking skeleton in the cloud": the per-product worker
boots in `azure` mode, is scoped to its product, reaches **real GitHub**, and
runs the full conveyor — a real, demoable deployment milestone.

## 3. Scope decisions (made autonomously — flagged for review)

**Decision A — Depth of `build_services('azure')` is deliberately thin.**
SP3 implements the azure **mode, settings resolution, scoping, and deployment**,
but **defers the heavy real-SDK service adapters** (Cosmos for memory, App
Configuration for config, App Insights for tracing, an LLM client for the model)
to a named follow-up (**SP3b**). Reasons: (a) the owner's north star is the
template+CLI that stamps out instances — deployment+scoping advances it directly,
real adapters are orthogonal runtime-fidelity work; (b) it keeps SP3 fully
**offline-testable** and pulls in **no new heavy cloud SDK dependencies**, whose
selection benefits from owner input; (c) it is a coherent, shippable increment.
In `azure` mode SP3 wires `github → RealGitHubClient` and keeps
`model/memory/config/tracer` on the existing fakes **behind a clear seam**, with
the resolved Azure settings carried on the bundle so the real adapters drop in
later. **First recommended follow-up adapter: App Insights tracer** (single dep,
high observability value).

**Decision B — Homelab is the implemented runtime target; ACA is a seam.**
Per ADR 0002 and the existing compose pattern (and `InstanceSpec.runtime_target`
already defaulting to `homelab` with `aca` as a choice), SP3 implements **homelab
compose rendering** and the renderer **dispatches on `runtime_target`**; `aca`
raises a clear "not yet (SP-later)" error. This honors the charter's "don't
hard-pick homelab vs ACA" by keeping the seam while shipping the default path.

**Decision C — Single-stage `--execute`, consistent with SP1/SP2.**
`deploy_council` renders the bundle always (pure), and under `--execute` brings
it up via an injected `docker compose … up -d` (offline-testable like
gh/squad/az). The rendered bundle is also left on disk for operator-applied
homelab hosts.

## 4. Architecture & components

Respecting the owner's requested **CLI/runtime separation**: rendering is
*provisioning/template tooling* → lives under `src/dsf/instance/`; the azure-mode
wiring + scoping + the runtime image are *factory-runtime* concerns → live under
`src/dsf/container.py`, `src/dsf/triggers/`, and a new runtime package. No new
provisioning logic is piled into `src/dsf/cli.py` beyond minimal flag wiring.

### 4.1 `AzureRuntimeSettings` + `build_services('azure')` (runtime)

- New `dsf.container.AzureRuntimeSettings` (pydantic) resolved from env. **Only
  `DSF_PRODUCT` is required in SP3** (it drives scoping); the endpoints
  `AZURE_APPCONFIG_ENDPOINT`, `AZURE_KEYVAULT_URI`,
  `APPLICATIONINSIGHTS_CONNECTION_STRING`, `AZURE_COSMOS_ENDPOINT` are **optional**
  (default empty) — they are carried for the deferred SP3b adapters and are not
  yet consumed by any port. A `from_env(env: Mapping)` classmethod (env injected
  for tests) validates and fails fast with a clear message if `DSF_PRODUCT` is
  missing/blank.
- `Services` gains `product: str | None = None` and
  `azure: AzureRuntimeSettings | None = None` (both `None` for local/gh).
- `build_services('azure', *, env=os.environ)` builds `AzureRuntimeSettings`,
  wires `github → RealGitHubClient`, keeps the other four ports on fakes (the
  deferred-adapter seam), and sets `product`/`azure` on the bundle. It no longer
  raises.

### 4.2 Single-product scoping (runtime)

- `triggers.scheduler.sweep`/`run_sweep`: when `services.product` is set, the
  built `Run` gets `product=services.product`, so synthesis routing, critic
  enablement, and the threshold all resolve to that product. When `None`,
  behavior is unchanged (multi-product, backward-compatible).
- `dsf --mode azure serve-orchestrator` therefore runs a product-pinned sweep
  with zero extra flags (product comes from `DSF_PRODUCT`).

### 4.3 Orchestrator runtime image (runtime)

- New `src/dsf/runtime/Dockerfile` mirroring the agent Dockerfiles (two-stage,
  non-root, python:3.12-slim pinned), with
  `CMD ["python", "-m", "dsf.cli", "--mode", "azure", "serve-orchestrator"]`.
  (`--mode` is a top-level flag and must precede the subcommand — verified
  against the argparse parser.)

### 4.4 Per-product runtime render + `deploy_council` (provisioning/template)

- New `src/dsf/instance/runtime_render.py`:
  `render_runtime_bundle(manifest, *, out_dir) -> RuntimeBundle` writes
  `compose.orchestrator.yml` + `.env.orchestrator` into
  `instances_dir(root)/<product>.runtime/`, parameterized from
  `manifest.azure.outputs` and `manifest.spec.product`. The compose service runs
  the orchestrator image with `DSF_PRODUCT`, the Azure endpoints, and
  `A2A_BEARER_TOKEN` placeholder; secrets are referenced from the env file, never
  inlined.
- `provisioner.plan()`: `deploy_council` becomes **active** (no longer deferred),
  described as "render per-product orchestrator runtime + bring up
  (`runtime_target`)". Deferred set shrinks to `{deploy_sre}`.
- `provisioner.apply()`: a `deploy_council` branch renders the bundle (always),
  and under `execute=True` dispatches on `spec.runtime_target`:
  `homelab` → `docker compose -f <compose> --env-file <env> up -d` via the
  injected runner; `aca` → raises a clear "ACA runtime target not yet supported
  (SP-later)". The rendered paths are recorded on the step result.

## 5. Data flow

`dsf new --product P --name-prefix … --execute`
→ SP2 provisions RG + Bicep, manifest gets `azure.outputs`
→ `deploy_council` renders `…/P.runtime/{compose.orchestrator.yml,.env.orchestrator}`
   from those outputs, pinned to `DSF_PRODUCT=P`
→ (homelab) `docker compose up -d` starts the orchestrator worker
→ worker boots `build_services('azure')` → product-scoped sweeps through the
   council conveyor → files real issues to `owner/P` via `gh`.

## 6. CLI surface

No new subcommand. `dsf new` already has `--runtime-target {homelab,aca}` (SP1);
SP3 makes it meaningful. `dsf --mode azure serve-orchestrator` gains product
scoping implicitly via `DSF_PRODUCT`. `build_services` error text for unsupported
modes updates to list `azure`.

## 7. Testing strategy

All offline, following existing patterns (injected `run`/`env`, fakes):

- `AzureRuntimeSettings.from_env`: valid env → populated; missing required →
  `ValueError` naming the gaps.
- `build_services('azure', env=…)`: returns a bundle with `RealGitHubClient`,
  `product` set, `azure` populated; does not raise; other ports are fakes.
- Scoping: `sweep` with `services.product='P'` → `run.product == 'P'`; with
  `None` → unchanged.
- `render_runtime_bundle`: writes both files; compose references `DSF_PRODUCT`
  and each Azure endpoint; env file carries the captured output values; no secret
  inlined.
- `provisioner.plan()`: `deploy_council` active; deferred == `{deploy_sre}`.
- `provisioner.apply(execute=True, runtime_target='homelab')`: renders bundle +
  invokes `docker compose … up -d` (asserted on the injected runner);
  `runtime_target='aca'` → raises the clear deferral error; dry-run renders
  nothing destructive and marks the step `dry-run`.
- Dockerfile presence/shape: a lightweight test asserts the runtime Dockerfile
  exists with the expected `CMD` (mirrors any existing agent-Dockerfile test, if
  present; otherwise a simple file assertion).

## 8. Out of scope (deferred)

- **SP3b**: real Azure service adapters — Cosmos (memory), App Configuration
  (config), App Insights (tracer), LLM (model) — each behind the seam established
  here. App Insights tracer recommended first.
- **ACA runtime target** rendering/deploy (seam present; raises until a later SP).
- **SRE agent** (`deploy_sre` stays deferred — SP5).
- Real homelab role-assignment/secret population and tunnel provisioning
  (operator-applied; the env file carries placeholders).
- The CLI/runtime package split (tracked separately as its own sub-project).

## 9. Files

Create:
- `src/dsf/runtime/__init__.py`, `src/dsf/runtime/Dockerfile`
- `src/dsf/instance/runtime_render.py`
- tests mirroring the above under `tests/`.

Modify:
- `src/dsf/container.py` (AzureRuntimeSettings, `Services.product/azure`,
  `build_services('azure')`).
- `src/dsf/triggers/scheduler.py` (product-scoped sweep).
- `src/dsf/instance/provisioner.py` (un-defer + render + bring-up branch).
- `src/dsf/instance/__init__.py` (export the render API as appropriate).
- Docs (folded into the SP3-wide documentation sweep): README layout, RUNBOOK
  "Creating a product instance", `infra/README.md`, charter SP3 row status.
