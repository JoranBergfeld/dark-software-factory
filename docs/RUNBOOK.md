# Dark Software Factory — Runbook

The intake line turns operational/market signals into grounded, deduplicated, labeled
GitHub issues. This runbook covers running it **locally** (no Azure, no LLM, no network)
and the steps to deploy to Azure **when you are awake to approve costs**.

## TL;DR

```bash
make install        # uv venv (py3.12) + editable install with dev deps
make test           # full test suite (fast, all in-memory)
make lint           # ruff
make dryrun         # run the whole intake line on a sample signal, dry-run
make evals          # run the golden-set eval gate
```

## What runs locally vs. what needs the cloud

Everything runs locally against **in-memory fakes** selected by `DSF_MODE=local`
(the default). The entire conveyor — investigation → synthesis → grounding gate →
council → routing → filing — executes deterministically with **no LLM and no network**.
`DSF_DRY_RUN=true` (default) means the filing station records the issue it *would*
file but never calls GitHub.

Azure implementations and IaC are authored but **not invoked** by the local flow.
They require your subscription, credentials, and explicit `azd up`.

| Capability | Local (now) | Azure (when you deploy) |
|---|---|---|
| Models / reasoning | deterministic `FakeModelClient` | Azure OpenAI via Foundry (`ModelClient` impl) |
| Memory (working + long-term + vector) | `FakeMemoryStore` (in-proc) | Cosmos DB |
| Control center config / flags | `FakeConfigStore` (`config/defaults.json`) | App Configuration + Feature Flags |
| Source backends (Sentry/Grafana/FoundryIQ/WebIQ) | fixture-backed fakes | MCP/SDK backends (`*McpBackend`) |
| GitHub issue filing | `FakeGitHubClient` (records, no network) | GitHub App |
| Tracing | `FakeTracer` (records spans) | OpenTelemetry → App Insights/Foundry |
| Tickets agent | stub (returns `[]`) | not yet integrated (designed-for) |

## Running pieces individually

```bash
# Run the line for one signal file (dry-run):
uv run python -m dsf.cli run --dry-run --signal tests/fixtures/sample_signal.json

# Scheduled sweep across enabled sources:
uv run python -m dsf.cli sweep

# Serve a source agent over A2A (FastAPI):
uv run python -m dsf.cli serve-agent --kind sentry --port 8080

# Serve the Control Center UI (toggles, thresholds, dry-run kill switch):
uv run python -m dsf.cli control-center --port 8081
# then open http://localhost:8081

# Serve the signal-ingestion endpoint (POST /ingest):
uv run uvicorn dsf.triggers.app:app --port 8082
```

## Creating a product instance (SP1 + SP2)

`dsf new` scaffolds an isolated product factory. `--name-prefix` is **required**;
it is sanitized and randomized into a <=12-char Azure resource prefix (persisted in
the manifest and reused on re-runs). Under `--execute`, repo creation + Coding Squad
init **and the dedicated Azure resource group + Bicep deployment** are real; feature
council and SRE deployment remain **deferred** stub steps (SP3/SP5).

```bash
# Preview the plan (no side effects):
uv run python -m dsf.cli new --product microbi --owner your-org --name-prefix microbi

# Preview AND write the instance manifest to config/instances/microbi.json:
uv run python -m dsf.cli new --product microbi --owner your-org --name-prefix microbi --write-plan

# Execute: create repo + init Squad + provision Azure (needs gh, @bradygaster/squad-cli, az):
uv run python -m dsf.cli new --product microbi --owner your-org --name-prefix microbi --execute
```

## The Control Center

The Control Center (`dsf.control_center.app`) is the **write** surface for runtime
behavior — it flips feature flags that take effect on the next run with no redeploy:

- enable/disable each of the 7 critics (globally / per product)
- enable/disable each source agent
- pause scheduled vs. signal triggers independently
- per-product confidence thresholds and council weights (with calibration proposals)
- the **global DRY-RUN kill switch** — run the full line but never file

The Grafana dashboard (`src/dsf/observability/grafana/dashboard.json`) is the
read-only observability surface; import it into Grafana once App Insights is wired.

## The learning loop

When a downstream spec PR is approved/rejected/edited, post the GitHub PR webhook to
the feedback watcher (`dsf.learning.feedback_watcher.handle_pr_event`). It distills the
verdict + proposed-vs-final spec diff into a product-scoped **Lesson** (retrieved by the
synthesizer/critics on the next run) and accumulates calibration data for council weights.

## Provisioning Azure + running in the homelab (do this awake — it costs money)

The topology (ADR 0002): Azure hosts **backing services only**; the agent/orchestrator
runtime runs in **your homelab** and reaches Azure **outbound**. No container is deployed
to Azure.

**1. Provision the Azure backing services** (`infra/README.md` has full detail):
```bash
az login && az group create -n rg-dsf-dev -l swedencentral
az deployment group what-if -g rg-dsf-dev -f infra/main.bicep -p @infra/main.parameters.json
az deployment group create  -g rg-dsf-dev -f infra/main.bicep -p @infra/main.parameters.json \
  -p workloadPrincipalId="<homelab-service-principal-object-id>"
```
This creates Cosmos, App Configuration, Key Vault, App Insights, and the Event Grid →
Service Bus ingestion buffer — and grants your homelab SP the data-plane roles.

**2. Wire CI (optional but recommended):** set repo variables `AZURE_CLIENT_ID`,
`AZURE_TENANT_ID`, `AZURE_SUBSCRIPTION_ID`, `AZURE_RESOURCE_GROUP` to activate the
`infra-whatif` pipeline's full preview on every infra change (OIDC, no secrets). The
`agents-images` pipeline already publishes each agent to `ghcr.io/<owner>/dsf-agent-<kind>`.

**3. Host the runtime in the homelab (your choice of orchestration):** pull the agent
images from GHCR (or build locally), set `DSF_MODE=azure`, authenticate with the homelab
service principal, and point config at the Bicep outputs (Cosmos/App Config/Key Vault/
App Insights endpoints). For real-time interrupts, poll the Service Bus `signals` queue
outbound; otherwise scheduled sweeps need nothing inbound. `infra/compose.homelab.yml`
shows the Grafana agent + tunnel as a starting point.

**4. Go live carefully:** keep `dry_run` ON in the Control Center until you trust a few
real runs, then turn it off to begin live filing.

## Guardrails before going live

- Keep the **dry-run kill switch** engaged for the first real-data runs and inspect the
  intended issues + kill log.
- The grounding gate (S4) and grounding critic (S5) both enforce that every filed claim
  traces to a real evidence citation — a down source yields *partial, flagged* evidence,
  never fabricated coverage.
- Per-run cost caps and dedup (S1/S5/S7) protect against floods and refiling.
