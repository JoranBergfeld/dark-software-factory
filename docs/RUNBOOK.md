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

Azure implementations and IaC are authored but **not invoked** by the *local* flow.
The Azure paths are reached deliberately: `dsf new <product> --execute` provisions a
per-product RG (SP2) and `--mode azure` runs the orchestrator against that product's
deployment outputs (SP3). Both require your subscription, credentials, and explicit opt-in.

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
uv run dsfctl run --dry-run --signal tests/fixtures/sample_signal.json

# Scheduled sweep across enabled sources:
uv run dsfctl sweep

# Serve a source agent over A2A (FastAPI):
uv run dsfctl serve-agent --kind sentry --port 8080

# Serve the Control Center UI (toggles, thresholds, dry-run kill switch):
uv run dsfctl control-center --port 8081
# then open http://localhost:8081

# Serve the signal-ingestion endpoint (POST /ingest):
uv run uvicorn dsf.triggers.app:app --port 8082
```

## Creating a product instance (SP1–SP5)

`dsf new` scaffolds an isolated product factory. `--name-prefix` is **required**;
it is sanitized and randomized into a <=12-char Azure resource prefix (persisted in
the manifest and reused on re-runs). Under `--execute`, repo creation + Coding Squad
init, **the dedicated Azure resource group + Bicep deployment**, and **rendering +
deploying the product's feature-council runtime** (an Azure Container App) are all
real. The SRE corner is **onboarded interactively** against the managed Azure SRE
Agent product (ADR 0009): the `onboard_sre_agent` step renders a per-product
runbook (`onboard_sre_agent` is render-only — no Container App is deployed).

```bash
# Preview the plan (no side effects):
uv run dsf new --product microbi --owner your-org --name-prefix microbi

# Preview AND write the instance manifest to config/instances/microbi.json:
uv run dsf new --product microbi --owner your-org --name-prefix microbi --write-plan

# Execute: create repo + init Squad + provision Azure + render/deploy council
# (needs gh, @bradygaster/squad-cli, and az for the Container App council):
uv run dsf new --product microbi --owner your-org --name-prefix microbi --execute
```

### The rendered per-product council runtime

`deploy_council` renders an Azure Container Apps descriptor to
`config/instances/<product>.runtime/` (a `containerapp.yaml` plus a resolved
`.env.orchestrator` populated from the product's Azure deployment outputs — endpoints
only; **secrets stay in Key Vault**, fetched at runtime via the user-assigned managed
identity, ADR 0004). The orchestrator Container App itself is created by `main.bicep`;
under `--execute`, `dsf new` rolls its image with
`az containerapp update --name dsf-orchestrator-<product> --image <runtimeImage>`.

The runtime image is `src/dsf/runtime/Dockerfile`; its entrypoint is the orchestrator in
**azure mode**, which reads endpoints from the rendered env and emits traces to
Application Insights (the OTel tracer is wired automatically when `DSF_MODE=azure`; it
degrades to a no-op fake if OpenTelemetry isn't importable). To run it by hand:

```bash
# global --mode MUST precede the subcommand:
DSF_PRODUCT=microbi uv run dsfctl --mode azure serve-orchestrator
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

## Council → Squad handoff

The council files issues into the product repo; the coding squad triages and
implements them. The contract is one system-level label —
`dsf.contracts.handoff.HANDOFF_LABEL` (`squad:ready`) — that S6 stamps on **every**
routed issue and that `squad triage` filters on (ADR 0007).

Provisioning wires this end to end: `create_labels` idempotently creates the
product's taxonomy labels + `squad:ready` in the repo (so filing never fails on a
missing label), and `squad triage --execute --label squad:ready` dispatches the
Copilot coding agent against council-filed issues. The full closed loop:

```
council files issue (squad:ready) → squad triage → Copilot agent → PR →
human review → council feedback-watcher → Lesson → next council run
```

## SRE agent (Azure SRE Agent product)

The production-watching corner of the factory is the managed **Azure SRE Agent**
product (ADR 0009), not a bespoke runtime. Onboarding is interactive — a wizard at
[sre.azure.com](https://sre.azure.com) plus browser-OAuth GitHub and Azure
resource-access grants — so `dsf new` renders a per-product runbook instead of
deploying anything:

- The provisioner's `onboard_sre_agent` step writes
  `config/instances/<product>.runtime/sre-onboarding.md` scoped to the product's
  resource group, region, and repo.
- Follow that runbook to create the agent (`dsf-sre-<product>`), connect the repo,
  and grant Reader on the product resource group.

The handoff is preserved: the Azure SRE Agent investigates incidents (Azure
Monitor / App Insights) and files GitHub issues/PRs carrying the `squad:ready`
label, so the **same** `squad triage --execute` intake picks them up.

```
prod telemetry → Azure SRE Agent → investigate → issue/PR (squad:ready)
→ squad triage → Copilot agent → PR
```

Render the onboarding runbook (offline; part of the normal plan):

```bash
uv run dsf new --product microbi --owner acme --name-prefix microbi \
  --execute   # writes sre-onboarding.md alongside the other runtime artifacts
```

## The learning loop

When a downstream spec PR is approved/rejected/edited, post the GitHub PR webhook to
the feedback watcher (`dsf.learning.feedback_watcher.handle_pr_event`). It distills the
verdict + proposed-vs-final spec diff into a product-scoped **Lesson** (retrieved by the
synthesizer/critics on the next run) and accumulates calibration data for council weights.

## Provisioning Azure + running the runtime (do this awake — it costs money)

The topology (ADR 0004): Azure hosts the **backing services** and the **runtime** — the
orchestrator runs as an Azure Container App in the same resource group, authenticating
with a user-assigned managed identity and reaching its sources over their authenticated
endpoints. No inbound ingress.

**1. Provision the backing services + runtime** (`infra/README.md` has full detail):
```bash
az login && az group create -n rg-dsf-dev -l swedencentral
az deployment group what-if -g rg-dsf-dev -f infra/main.bicep -p @infra/main.parameters.json
az deployment group create  -g rg-dsf-dev -f infra/main.bicep -p @infra/main.parameters.json \
  -p product=microbi -p runtimeImage="ghcr.io/<owner>/dsf-runtime:latest"
```
This creates Cosmos, App Configuration, Key Vault, App Insights, the Event Grid →
Service Bus ingestion buffer, the **user-assigned identity** (granted the data-plane
roles), and the **Container Apps environment + `dsf-orchestrator-microbi` app**.

**2. Wire CI (optional but recommended):** set repo variables `AZURE_CLIENT_ID`,
`AZURE_TENANT_ID`, `AZURE_SUBSCRIPTION_ID`, `AZURE_RESOURCE_GROUP` to activate the
`infra-whatif` pipeline's full preview on every infra change (OIDC, no secrets). The
`agents-images` pipeline already publishes each agent to `ghcr.io/<owner>/dsf-agent-<kind>`.

**3. Roll the orchestrator image:** publish the runtime image (`src/dsf/runtime/Dockerfile`)
to GHCR, then update the Container App in place:
```bash
az containerapp update -g rg-dsf-dev -n dsf-orchestrator-microbi \
  --image ghcr.io/<owner>/dsf-runtime:latest
```
The app runs in **azure mode** with `DSF_MODE=azure` and `AZURE_CLIENT_ID` set to the
identity, reading endpoints from its env and polling the Service Bus `signals` queue
outbound for real-time interrupts; scheduled sweeps need nothing inbound.

**4. Go live carefully:** keep `dry_run` ON in the Control Center until you trust a few
real runs, then turn it off to begin live filing.

## Guardrails before going live

- Keep the **dry-run kill switch** engaged for the first real-data runs and inspect the
  intended issues + kill log.
- The grounding gate (S4) and grounding critic (S5) both enforce that every filed claim
  traces to a real evidence citation — a down source yields *partial, flagged* evidence,
  never fabricated coverage.
- Per-run cost caps and dedup (S1/S5/S7) protect against floods and refiling.
