# Remove the homelab runtime target → Azure Container Apps (design)

> Cleanup workstream #28 (issue
> [#28](https://github.com/JoranBergfeld/dark-software-factory/issues/28)), first
> of the "cleanups before SPs" re-baseline. Establishes the Azure-hosted runtime
> that the redefined SP3b → SP5 build on. Brainstormed autonomously; owner reviews
> the written spec (per-workstream spec-approval gate).

## 1. Context & current state

Per **ADR 0002**, the factory runtime (orchestrator + source agents + control
center) is hosted in the user's **homelab** (Proxmox / docker compose) and reaches
Azure **backing services only** outbound. That decision drives a chain of design:

- `infra/main.bicep` provisions **no compute** — Cosmos, App Configuration, Key
  Vault, Log Analytics/App Insights, Event Grid→Service Bus only.
- Homelab auth is an **Entra service principal** (`workloadPrincipalId`), because
  Managed Identity only works inside Azure. Its object id receives the data-plane
  roles (Cosmos/App Config/Key Vault/Service Bus). Referenced in
  `infra/main.bicep`, `infra/main.parameters.json`, `infra/README.md`,
  `src/dsf/instance/{spec,provisioner}.py`, `src/dsf/cli/factory.py`.
- SP3 renders each product's runtime as a **homelab compose bundle**
  (`render_runtime_bundle` → `compose.orchestrator.yml` + `.env.orchestrator`)
  and `deploy_council` brings it up with `docker compose up` when
  `runtime_target == "homelab"`.
- `runtime_target` defaults to `"homelab"` (`spec.py:35`); `factory.py:81-83`
  already lists `"aca"` as a second, **unimplemented** choice (the seam SP3 kept
  open). `infra/compose.homelab.yml` + `infra/.env.homelab.example` host the
  Grafana agent behind an egress-only tunnel sidecar.

Total: **~179 `homelab` references** across ADRs, infra, source, docs, and tests.

The owner has decided homelab is **no longer relevant**: the runtime moves into
Azure, hosted on **Azure Container Apps (ACA)**.

## 2. Goal

1. Remove **all** homelab mentions and implementation.
2. Make **Azure Container Apps** the runtime host: the per-product orchestrator
   runtime (and source agents) run as Container Apps in the product's resource
   group, reaching the backing services in-Azure.
3. Because the runtime now runs **inside** Azure, flip workload auth from the
   Entra service principal (`workloadPrincipalId`) to a **system-assigned Managed
   Identity** on the Container App(s), which receives the data-plane roles. This
   removes the SP plumbing and the tunnel/egress-only model entirely.
4. Record the reversal in a new **ADR 0004 (supersedes ADR 0002)**.

This yields a coherent "runtime in Azure" base with no homelab left, ready for the
redefined SP3b (real data adapters) and SP4/SP5.

## 3. Scope decisions (made autonomously — flagged for review)

**Decision A — ACA hosting belongs to THIS workstream, not SP3b.** SP3b stays
focused on the data-plane adapters (App Config / Cosmos / LLM). Compute/hosting is
the direct homelab replacement, so the ACA Bicep + deploy path lands here. Keeps a
clean seam: #28 = *where the runtime runs*, SP3b = *what the runtime talks to*.

**Decision B — Managed Identity replaces `workloadPrincipalId`.** The SP path
existed only because homelab is outside Azure (ADR 0002 §4). In ACA we use a
system-assigned MI and assign the existing data-plane roles to it. The
`workloadPrincipalId` parameter and all its plumbing are removed; the Bicep wires
the role assignments to the Container App's `identity.principalId` directly, so
there is no longer an "empty = skip role assignment" path.

**Decision C — Full retarget, minimal footprint (recommended).** `infra/main.bicep`
gains a **Container Apps Environment + one Container App** (the orchestrator
runtime image), system-assigned MI, role assignments to that MI. `deploy_council`
deploys via the injected runner (`az containerapp …` / `az deployment group
create`) under `--execute`, dry-run otherwise — mirroring SP3's homelab path.
`render_runtime_bundle` emits an **ACA container-app config** (`containerapp.yaml`
+ rendered env) instead of a compose file.
_Lighter alternative if you want #28 truly minimal:_ remove homelab + retarget the
render/ADR only, and defer the ACA Bicep compute + real deploy into redefined
SP3b. **Recommendation: full retarget**, so the repo never sits in a
"runtime has no host" state. (Flag at review if you prefer the lighter cut.)

**Decision D — Source agents run in ACA too; the tunnel is gone.** `compose.homelab.yml`
(Grafana agent + cloudflared/tailscale tunnel) is removed. Agents reach their data
sources (Grafana/Sentry/…) over their existing authenticated public endpoints in
`live` mode; nothing inbound is needed since the orchestrator and agents are
co-located in Azure.

**Decision E — No new `src/` fakes.** Even though the fakes-out workstream (#27)
is next, this workstream introduces **no** new fake classes; tests use the
existing injected-runner DI pattern (`subprocess.run` seam) only.

**Decision F — Remove the `homelab-dash` demo product entirely (owner-directed).**
"Homelab" appears in a *second*, unrelated guise: a sample product literally named
`homelab-dash` (Grafana dashboard `homelab-overview`) woven through
`config/products.json`, the **golden eval cases** (`src/dsf/evals/golden/cases.json`),
the grafana evidence fixture (`tests/fixtures/grafana_evidence.json`), and the
registry/grafana/flags/eval tests. The owner directed removing this demo product
*entirely* from the registry/fixtures. `microbi` remains the sole demo product, so
the registry stays non-empty. **To preserve GRAFANA-source + multi-source eval
coverage** (the GRAFANA agent itself stays — `microbi` declares
`grafana_dashboards`), the grafana sample evidence and the grafana golden case are
**re-scoped to `microbi`** rather than deleted; citation hosts move off
`grafana.homelab.lan` to a neutral `grafana.example.com`. The eval gate
(`uv run python -m dsf.evals.runner --gate`) must stay green after re-scoping.

## 4. Design / changes

### Infra (`infra/`)
- `main.bicep`: add `Microsoft.App/managedEnvironments` + `Microsoft.App/containerApps`
  (orchestrator runtime; image ref + env parameterized), system-assigned identity;
  reassign Cosmos/App Config/Key Vault/Service Bus data-plane roles from
  `workloadPrincipalId` → the Container App's `identity.principalId`. Remove the
  `workloadPrincipalId` param (and from `main.parameters.json`).
- Delete `infra/compose.homelab.yml` and `infra/.env.homelab.example`.
- `infra/README.md`, `infra/azure.yaml`, `infra/modules/ingestion.bicep`: drop
  homelab framing; document the ACA runtime + MI auth.

### Instance tooling (`src/dsf/instance/`)
- `spec.py`: `runtime_target` default `"aca"`; remove `"homelab"`. Remove
  `workload_principal_id` and its `resource_group()`-adjacent plumbing.
- `runtime_render.py`: render an **ACA app config** (`containerapp.yaml`) +
  `.env` instead of `compose.orchestrator.yml`; `RuntimeBundle` fields renamed
  (`app_config_path`/`env_path`). `DSF_MODE=azure` retained.
- `provisioner.py`: `deploy_council` deploys to ACA via the injected runner under
  `--execute`; the `runtime_target != "homelab"` `NotImplementedError` branch is
  removed (ACA is now the implemented default).

### CLI (`src/dsf/cli/factory.py`)
- `--runtime-target` default `"aca"`, `choices=["aca"]` (single target, extensible);
  drop homelab wording in the module docstring.

### Agents / misc (`src/dsf/`)
- Remove homelab comments/branches in `agents/grafana/{backend,main,__init__}.py`,
  `agents/sentry/mcp_client.py`, `runtime/__init__.py`.

### Demo data / fixtures (Decision F)
- `config/products.json`: delete the entire `homelab-dash` product object; `microbi`
  remains the sole demo product.
- `src/dsf/evals/golden/cases.json`: re-scope the grafana case
  `grafana-homelab-latency` → `grafana-microbi-latency` (`expected_product` /
  `product_hints` → `microbi`); drop the `homelab-dash` hint from the
  `sentry-grafana-multi-source` case (microbi-only). No homelab strings remain.
- `tests/fixtures/grafana_evidence.json`: `product_hints` `homelab-dash` → `microbi`;
  citation host `grafana.homelab.lan` → `grafana.example.com`.
- `tests/config/test_{registry,flags}.py`, `tests/evals/test_runner.py`,
  `tests/agents/grafana/*`: drop `homelab-dash`/`homelab-overview` assertions and
  fixtures; assert against `microbi` instead.

### Docs & ADRs
- New **`docs/adr/0004-azure-container-apps-runtime.md`** (Status: Accepted;
  *Supersedes ADR 0002*): runtime hosted on ACA in the product RG; MI data-plane
  auth; no homelab/tunnel/SP.
- Mark `docs/adr/0002-...md` **Superseded by ADR 0004** (keep for history).
- Update homelab mentions in `docs/adr/0001-...md`, `README.md`, `docs/RUNBOOK.md`,
  `src/dsf/evals/README.md`, and the charter §8 Open Decision #1.
- Historical `docs/superpowers/specs|plans/*` are **not** rewritten (they are dated
  records); the new ADR + this spec are the current source of truth.

## 5. Testing (offline, no billable resources, no new fakes)

- `tests/instance/test_runtime_render.py`: asserts ACA config + env rendered (no
  compose); `DSF_MODE=azure`, product scoping, blank-tolerant endpoints.
- `tests/instance/test_provisioner.py`: `deploy_council` issues the `az containerapp`
  deploy via a **fake injected runner** under `--execute`; dry-run renders only.
- `tests/instance/test_spec.py`: default `runtime_target == "aca"`; no homelab.
- Update `tests/config/test_{registry,flags}.py`, `tests/evals/test_runner.py`,
  `tests/agents/grafana/*` to drop homelab fixtures.
- Bicep is validated structurally where already covered; no live Azure calls.

## 6. Out of scope (separate workstreams)
- Real data-plane adapters (App Config/Cosmos/LLM) — **redefined SP3b**.
- Broad removal of `src/dsf/fakes` production fakes — **#27** (next).
- Splitting into standalone apps — **#26** (later/structural).

## 7. Done when
- `grep -rni homelab` over the whole repo **except** the dated
  `docs/superpowers/specs|plans/*` records returns **0** (covers `src/`, `infra/`,
  `config/`, `tests/`, `docs/adr/`, `README.md`, `docs/RUNBOOK.md`, golden cases &
  fixtures).
- `runtime_target` is `"aca"` end-to-end; `deploy_council` deploys to ACA via the
  injected runner (dry-run by default); MI replaces `workloadPrincipalId`.
- The `homelab-dash` demo product is gone; `microbi` is the sole demo product and
  GRAFANA/multi-source eval coverage is preserved (re-scoped to `microbi`).
- ADR 0004 added; ADR 0002 marked superseded.
- `uv run ruff check .` clean, `uv run pytest -q` green, and
  `uv run python -m dsf.evals.runner --gate` PASSES.
