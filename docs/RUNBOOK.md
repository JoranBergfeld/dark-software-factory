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

## Deploying to Azure (do this awake — it costs money)

1. `az login` and select the target subscription.
2. Review `infra/README.md` and `infra/main.bicep`. Set params: `namePrefix`,
   `location`, and the **existing** Azure OpenAI/Foundry endpoint + deployment
   (the template references them; it does not create model deployments).
3. Validate without deploying: `az deployment group create --what-if ...`.
4. Provision + deploy: `azd up` (provisions Container Apps, Cosmos, App Configuration,
   Key Vault, App Insights, Event Grid, managed identity + RBAC, and pushes images).
5. Deploy the homelab Grafana agent: `docker compose -f infra/compose.homelab.yml up -d`
   after configuring the tunnel (`infra/.env.homelab.example`).
6. Set `DSF_MODE=azure`. Keep `dry_run` ON in the Control Center until you trust a few
   real runs, then turn it off to begin live filing.

## Guardrails before going live

- Keep the **dry-run kill switch** engaged for the first real-data runs and inspect the
  intended issues + kill log.
- The grounding gate (S4) and grounding critic (S5) both enforce that every filed claim
  traces to a real evidence citation — a down source yields *partial, flagged* evidence,
  never fabricated coverage.
- Per-run cost caps and dedup (S1/S5/S7) protect against floods and refiling.
