# Dark Software Factory

Inspired by manufacturing "dark factories," this repo automates the hardest part of the
SDLC: **deciding what to build.** It's a mostly-autonomous, multi-product pipeline that
listens to operational and market signals, investigates them with rigorous grounding,
runs proposals past an adversarial critic council, and files labeled GitHub issues (dry-run
by default; real filing via ``--mode gh`` or ``--mode azure``) with a full evidence trail.
The only human gate is downstream, approving the spec PR.

This repo is also a **template + CLI**: `dsf new <product>` stamps out an *isolated*
software factory for one product — its own GitHub repo + Coding Squad, a feature council
scoped to that product, a dedicated Azure resource group, and the council runtime
deployed as an Azure Container App. See the
[charter](docs/superpowers/specs/2026-06-17-dark-software-factory-template-charter-design.md)
for the north-star and roadmap (SP1–SP5 implemented; instance lifecycle next).

```
 signals ──▶ [S1 Triage] ─▶ [S2 Investigation] ─▶ [S3 Synthesis] ─▶ [S4 Grounding gate]
          ─▶ [S5 Critic Council] ─▶ [S6 Routing] ─▶ [S7 Filing] ──▶ labeled GitHub issue
```

Investigation gathers evidence from **Sentry, Grafana, FoundryIQ, WebIQ** (each a portable
A2A agent deployed near its source). A 7-member critic council (grounding, value,
duplication, feasibility, strategic-fit, cost-to-build, security/compliance) scores and
can veto. A **Control Center** UI toggles critics/agents/triggers and a global dry-run
kill switch at runtime. A **learning loop** feeds human PR verdicts back as retrievable
lessons and council-weight calibration.

## Quickstart

```bash
make install   # uv venv (Python 3.12) + editable install
make test      # full suite (in-memory, no cloud, no LLM)
make dryrun    # run the whole line on a sample signal, dry-run
make evals     # golden-set eval gate
```

Then explore the Control Center:

```bash
uv run dsfctl control-center   # http://localhost:8081
```

Or stamp out a product factory (dry-run by default; `--execute` is destructive):

```bash
uv run dsf new --product microbi --owner your-org --name-prefix microbi   # preview the 8-step plan
```

Two CLIs ship from the single `dsf` package: **`dsf`** creates/manages product
instances from the template (`dsf new`), and **`dsfctl`** operates a running
instance's feature-council runtime (`dsfctl run|sweep|serve-orchestrator|serve-agent|
control-center`).

`dsf new` creates the product GitHub repo + Coding Squad, provisions a dedicated Azure
resource group from `infra/main.bicep`, and renders + deploys the product's council
runtime as an Azure Container App (see RUNBOOK). For the production-watching corner it
renders a per-product onboarding runbook for the managed **Azure SRE Agent** product
(ADR 0009), whose incident issues carry the `squad:ready` handoff label.

## Status

- **Runnable locally, end-to-end, in dry-run** against in-memory fakes — every component
  built behind a port. The full conveyor drives a signal to a (simulated) filed issue with
  grounding enforced and no network calls. All tests pass (run ``make test`` for the current count).
- **Azure-ready, not deployed.** Bicep provisions the **backing services** (Cosmos, App
  Config, Key Vault, App Insights, Event Grid → Service Bus ingestion buffer) **and the
  runtime** — a Container Apps environment + orchestrator app on a user-assigned managed
  identity (ADR 0004). An `infra-whatif` pipeline previews infra changes (OIDC) and an
  `agents-images` pipeline publishes each agent to GHCR. No billable resources are
  provisioned by the local flow.
- **Per-product factories.** `dsf new <product>` provisions an isolated Azure RG per
  product (backing services + runtime identity + orchestrator Container App) and renders
  that product's runtime descriptor to `config/instances/<product>.runtime/`. In `--mode
  azure` the orchestrator reads endpoints from the product's deployment outputs and emits
  traces to Application Insights. Real Cosmos/App Config/LLM adapters land in SP3b.

## Docs

- Design spec: `docs/superpowers/specs/2026-06-15-dark-software-factory-intake-design.md`
- Implementation plan: `docs/superpowers/plans/2026-06-15-dark-software-factory-intake.md`
- **Template + CLI charter: `docs/superpowers/specs/2026-06-17-dark-software-factory-template-charter-design.md`**
- **Runbook (how to run & deploy): `docs/RUNBOOK.md`**
- Architecture decisions: `docs/adr/0001-architecture-decisions.md` ·
  `docs/adr/0002-homelab-runtime-azure-backing-only.md` (superseded) ·
  `docs/adr/0004-azure-container-apps-runtime.md`

## Layout

`src/dsf/` — `contracts` (shared models) · `ports` + `fakes` · `config` (flags + product
registry) · `memory` (tiers, dedup, consolidation) · `a2a` · `agents/<source>` ·
`council` (synthesizer + critics + decision) · `orchestrator` (conveyor + stations) ·
`triggers` · `learning` · `evals` · `observability` · `control_center`.
`instance/` — instance spec + provisioner powering the `dsf new` CLI (greenfield
product-factory scaffolding; creates the product repo + Coding Squad, provisions a
dedicated per-product Azure resource group from `infra/main.bicep`, and renders + deploys
the product's council runtime as an Azure Container App; only SRE-agent deployment is
deferred to a later sub-project).
`runtime/` — the orchestrator runtime image (`Dockerfile`) the rendered Container App runs.
`infra/` — Bicep/azd (backing services + ACA runtime).
