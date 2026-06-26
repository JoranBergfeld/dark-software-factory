# Operate the factory

A factory you have [provisioned](provision-a-factory.md) runs itself. The council sweeps its
sources on a schedule, files grounded `creation:ready` issues, the Creation phase builds them, and
the SRE Agent watches production and feeds incidents back to the start. No one stands inside
the pipeline.

Operating it means **governing from outside** — steering through the harness (flags,
thresholds, the dry-run kill switch), watching the telemetry, and trusting the closed loop to
run. This page covers the deployed runtime and the surfaces you govern it with.

## The runtime

`dsf new` deploys the council runtime as an **Azure Container App**
(`<namePrefix>-orchestrator`) in the product's resource group, authenticating with a
user-assigned managed identity and reading every endpoint from its environment — App
Configuration, Cosmos, Key Vault, App Insights (ADR 0004). Secrets never land in the
descriptor; they stay in Key Vault and are fetched at runtime through the identity. The
`dsf new` laptop flow enables public network access for the backing services so the CLI,
runtime, and `dsf charter` commands can reach them; AAD/RBAC still gate access and local auth
stays disabled. Private-endpoint deployments should deploy `infra/main.bicep` directly with
`allowPublicNetworkAccess=false`.
The runtime is **real-only**: `build_services()` requires `DSF_PRODUCT` plus the Azure endpoints
and never falls back to a stub (ADR 0014).

DSF is **pull-only**. The orchestrator gets work by *sweeping* its enabled source agents —
there is no inbound ingress and no pushed inbox. The deployed app runs the sweep loop
continuously:

```bash
# how the Container App runs (sweep forever; tune cadence with DSF_SWEEP_INTERVAL):
DSF_PRODUCT=<product> uv run dsfctl serve-orchestrator --loop
```

The runtime image is built from `feature-council/src/dsf/runtime/Dockerfile`; `dsf new` rolls
it onto the Container App with `az containerapp update`. Rendered runtime descriptors land in
`config/instances/<product>.runtime/` (a `containerapp.yaml` plus a resolved
`.env.orchestrator` — endpoints only). See [Provision a factory](provision-a-factory.md) for
how all of this is stood up.

## The product charter

The charter (`.dsf/charter.md` in the product repo) states what the product is
for. The runtime syncs it on every sweep — deterministically and fail-safe: if
the file is missing or unparsable, the council keeps the last good charter and
flags the status instead of dropping it. So an operator never has to push the
charter; merging it to the product repo is enough.

Operator commands:

- `dsf charter init --product <p>` — interview, then open a PR adding the charter.
  `dsf new` offers to chain into this automatically on a greenfield factory (skip it
  with `--no-charter` or a non-interactive shell); the charter only becomes
  authoritative once the PR is **merged** and the next `dsfctl sweep` syncs it.
- `dsf charter sync --product <p>` — force a sync now (otherwise the next sweep
  does it).
- `dsf charter status --product <p>` — print the stored charter's status and any
  drift vs the file (`OK` / `STALE` / `MISSING` / `INVALID`).

`dsf charter init` (and `sync`/`status --ref`) reach the product repo through the
master DSF GitHub App. Keep `DSF_OWNER_KEYVAULT_URI` exported (the value `dsf
bootstrap` printed and that `dsf new` already uses): the CLI then reads the App id,
installation id, and private key from that owner Key Vault and resolves the repo
from the registry — no other GitHub env vars are needed. Explicit `GITHUB_APP_ID` /
`GITHUB_INSTALLATION_ID` / `GITHUB_APP_PRIVATE_KEY_SECRET` / `AZURE_KEYVAULT_URI`
override it when set.

The Azure backing-service endpoints (App Configuration, Cosmos, and Azure OpenAI)
are auto-loaded the same way: `dsf charter` reads them from the product's instance
manifest (`config/instances/<product>.json`, written by `dsf new`), so a
freshly-provisioned product needs no `AZURE_*` exports. Explicit
`AZURE_OPENAI_ENDPOINT` / `AZURE_OPENAI_DEPLOYMENT` / `AZURE_APPCONFIG_ENDPOINT` /
`AZURE_COSMOS_ENDPOINT` override the manifest when set.

The charter is **advisory**: it informs the council's value and strategic-fit
reasoning and adds a non-blocking "possible non-goal conflict" note to a
verdict's rationale. It never vetoes a proposal and never changes the score.
Products without a charter run exactly as before (tagged `uncharted product context`).
See [Product Charter](../concept/product-charter.md) and ADR 0017.

### Living charter (proposed amendments)

When `charter.amendment.enabled` is set for a product, the sweep can **propose**
charter amendments from accumulated lessons — strictly as a human-gated
governance PR against `.dsf/charter.md`. The factory never edits the stored
charter; only merging the PR syncs it (the pull-only path above).

- Off by default — opt in with a control-center override of
  `charter.amendment.enabled` per product.
- Tunables: `charter.amendment.min_lessons` (evidence threshold, default 3) and
  `charter.amendment.cooldown_hours` (min interval between proposals, default 168).
- Guardrails (deterministic): one open amendment PR per product, the cooldown,
  an evidence bundle attached to the PR, and `governance` + `charter-amendment`
  labels for anti-rubber-stamp review (require a reviewer who is **not** the
  proposer via branch protection / CODEOWNERS).
- Each sweep records the decision on the run audit (`trigger:charter-amend`):
  `proposed PR <url>`, `disabled`, `cooldown`, `open_pr`, `no_change`, etc.

See ADR 0018.


## Steering: the Control Center

The Control Center (`dsf.control_center.app`) is the **write surface** for runtime behaviour —
the dial operators steer with. It flips feature flags that take effect on the *next* run, with
no redeploy:

- enable/disable each of the **7 council critics** (globally or per product)
- enable/disable each **source agent**
- pause **scheduled** vs **signal** triggers independently
- per-product **confidence thresholds** and **council weights**, with calibration proposals
  surfaced from the learning loop
- the **global dry-run kill switch** — run the full line but never file

Write routes require a bearer token and a CSRF check, and every flag change emits a structured
audit line. You change *policy*; the running factory adapts on its next sweep — nobody edits
the pipeline.

## Watching it

The orchestrator emits OpenTelemetry traces to **Application Insights** automatically
(`build_services()` wires the tracer; it degrades to a no-op if OpenTelemetry isn't present).
The read-only dashboard at `core/src/dsf/observability/grafana/dashboard.json` imports into
Grafana once App Insights is wired.

## The closed loop: council → creation

The council files issues into the product repo and assigns the GitHub Copilot Coding Agent
through the DSF GitHub App. The whole contract is one system label —
`dsf.contracts.handoff.HANDOFF_LABEL` (`creation:ready`) — that S6 stamps on **every**
routed issue and that the Coding Agent picks up (ADR 0007, ADR 0016).

`dsf new` wires this end to end: `register_product` upserts the product (repo + label taxonomy
+ confidence threshold) into the routing registry that S1 scoping and S6 routing read;
`create_labels` idempotently creates the taxonomy + `creation:ready` so filing never fails on a
missing label; and the DSF GitHub App assigns the Copilot Coding Agent when S7 files the issue.
`dsf new` also creates the `dsf-creation` branch-protection ruleset from the
`creation_maturity` dial: low means required human review plus green `ci`; high means
auto-merge on required checks.

The full loop:

```
council files issue (creation:ready) + assigns Copilot Coding Agent → Coding Agent PR →
creation_maturity ruleset gates merge → feedback-watcher → Lesson → next council run
```

## Production: the SRE Agent

The production-watching corner is the managed **Azure SRE Agent** product, not a bespoke
runtime. `dsf new` provisions it via `deploy_sre_agent` (`az deployment sub create` with
`infra/sre-agent.bicep`), which creates a dedicated `rg-dsf-sre-<product>`, a managed identity
bound to the `Microsoft.App/agents` resource, the read roles it needs on the monitored
resource groups plus Monitoring Contributor at subscription scope, and the Azure Monitor
connectors (ADR 0015). The agent can be scoped to a resource group or to a whole subscription.

It investigates incidents (Azure Monitor / App Insights) and files issues/PRs carrying
`creation:ready` — so the Copilot Coding Agent picks them up — plus an `incident` label that the council's
`incidents` and `azuremonitor` sources pull on the council's own schedule. Recurring
production faults therefore become systemic hardening proposals, not just one-off fixes
(ADR 0013).

```
prod telemetry → Azure SRE Agent → investigate → issue/PR (creation:ready) → Copilot Coding Agent → PR
prod incidents  → issue (incident) + Azure Monitor → council incidents/azuremonitor
                  → S1–S7 → creation:ready proposal
```

## The learning loop

When a downstream PR is approved, rejected, or edited, the product's PR webhook reaches the
feedback watcher (`dsf.learning.feedback_watcher.handle_pr_event`). It distills the verdict
plus the proposed-vs-final diff into a product-scoped **Lesson** (retrieved by the synthesizer
and critics on the next run) and accumulates calibration data that the Control Center surfaces
as proposed council weights. The factory tunes itself; operators approve the calibration from
outside the loop.

## Guardrails

The factory ships with standing guardrails you govern rather than babysit:

- the **dry-run kill switch** (Control Center) — run the full line but file nothing; engage it
  for a product's first real-data runs and inspect the intended issues and the kill log
- the **grounding gate (S4) and grounding critic (S5)** — every filed claim must trace to a
  real evidence citation; a down source yields *partial, flagged* evidence, never fabricated
  coverage
- **per-run cost caps and dedup (S1/S5/S7)** — protection against signal floods and refiling
