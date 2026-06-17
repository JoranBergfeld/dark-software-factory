# SP2 — Per-Product Azure Provisioning (Design)

> Sub-project of the Dark Software Factory **template + CLI** charter
> (`docs/superpowers/specs/2026-06-17-dark-software-factory-template-charter-design.md`).
> Builds directly on **SP1** (`dsf new` greenfield walking skeleton).

**Scope:** Turn the SP1 `provision_azure` *deferred stub* into a real, idempotent
step: `dsf new --execute` creates a dedicated resource group per product, deploys
the existing parameterized Bicep (`infra/main.bicep`) into it, and captures the
deployment outputs into the instance manifest. Council and SRE deployment remain
deferred (SP3 / SP5).

---

## Problem

SP1 produces a real instance shell, but its `provision_azure` step is a
`deferred=True` stub (it only carried an illustrative `az group create` command and
never ran). The Azure backing services the feature council needs — Cosmos, App
Configuration, Key Vault, Application Insights, and the Event Grid→Service Bus
ingestion buffer — are already authored in `infra/main.bicep` and well
parameterized (`namePrefix`, `location`, `environmentName`, `workloadPrincipalId`).
What is missing is the CLI/provisioner wiring that actually deploys them **per
product, into an isolated RG, idempotently, and records where they live** so later
sub-projects (SP3's `build_services('azure')`) can consume them.

## Locked decisions (from brainstorming)

1. **Provisioning mechanism:** raw `az` CLI — `az group create` + `az deployment
   group create`, capturing outputs via `--query properties.outputs -o json`.
   Mirrors SP1's injectable-runner pattern; tests stay fully offline.
2. **Aggressiveness:** **single-stage** — `dsf new --execute` provisions Azure for
   real, exactly like it already does for `gh`/`squad`. The dry-run default (no
   `--execute`) remains the spend guard.
3. **Resource naming:** `--name-prefix` is **required** on every `dsf new`. The
   effective Bicep `namePrefix` = `sanitize(base)` (lowercase alphanumeric),
   substringed to ≤8 chars, **+ 4 random chars** (total ≤12, must start with a
   letter). This dodges global-name collisions and Key Vault's 90-day soft-delete
   name reservation on re-creation. The effective prefix is **generated once,
   persisted in the manifest, and reused on re-runs** so provisioning is
   idempotent.

## Architecture

### Naming (`make_name_prefix`)

A pure helper isolates the randomness so `InstanceProvisioner.plan()` stays
side-effect-free and deterministic:

```python
def make_name_prefix(base: str, *, token: str | None = None, token_len: int = 4) -> str:
    cleaned = "".join(c for c in base.lower() if c.isalnum())
    # ...require it to start with a letter; substring stem to (12 - token_len)...
    stem = cleaned[: 12 - token_len]
    tok = token if token is not None else _random_token(token_len)
    return f"{stem}{tok}"
```

- `_random_token` draws from `secrets.choice` over `[a-z0-9]`.
- Result is validated against `^[a-z][a-z0-9]{2,11}$`.
- The **CLI** (`_cmd_new`) calls it once; tests inject `token=` for determinism.

### InstanceSpec (extended)

New fields (all with defaults so existing programmatic construction keeps working):

| field | default | notes |
| --- | --- | --- |
| `name_prefix` | `"dsf"` | effective prefix; validated `^[a-z][a-z0-9]{2,11}$` |
| `environment` | `"dev"` | Bicep `environmentName` |
| `location` | `"swedencentral"` | Azure region / RG location |
| `workload_principal_id` | `""` | passthrough to Bicep (empty ⇒ provision-only, no role assignments) |

`resource_group()` is unchanged (`rg-dsf-<product>`): RGs carry no soft-delete name
reservation, so they stay deterministic per product.

### Plan (un-defers Azure into two active steps)

The ordered step list becomes (8 steps; one command per step):

| # | step | active? | command |
| --- | --- | --- | --- |
| 1 | `create_repo` | yes | `gh repo create …` |
| 2 | `squad_init` | yes | `squad init …` |
| 3 | `squad_copilot` | yes | `squad copilot …` |
| 4 | `create_resource_group` | **yes (new)** | `az group create --name rg-dsf-<product> --location <loc>` |
| 5 | `provision_azure` | **yes (was deferred)** | `az deployment group create -g <rg> -n dsf-<product> -f <root>/infra/main.bicep -p namePrefix=<eff> environmentName=<env> location=<loc> workloadPrincipalId=<id> --query properties.outputs -o json` |
| 6 | `deploy_council` | deferred (SP3) | — |
| 7 | `deploy_sre` | deferred (SP5) | — |
| 8 | `write_config` | yes | writes `config/instances/<product>.json` |

The deployment name is deterministic (`dsf-<product>`) for idempotency/traceability.
The Bicep path resolves from the provisioner's repo root (real repo root when
`repo_root=None`, the template's `infra/main.bicep`).

### apply() and output capture

- **`AzureProvisionResult`** (new pydantic model): `resource_group: str`,
  `deployment_name: str`, `location: str`, `outputs: dict[str, str]`.
- **`InstanceManifest`** gains `azure: AzureProvisionResult | None = None`.
- In `apply()`, the `provision_azure` step is special-cased (like `create_repo`):
  on `execute=True` it runs with `capture_output=True, text=True`, and the stdout
  JSON (`{"cosmosEndpoint": {"type": "...", "value": "..."}, …}`) is flattened to
  `{name: value}` via a `_parse_outputs` helper and stored on the manifest.
- `create_resource_group` runs as an ordinary commanded step.
- **Dry-run** (`execute=False`): both az steps report `"dry-run"`; `manifest.azure`
  stays `None`.
- **Idempotency:** `az group create` and incremental `az deployment group create`
  are server-side idempotent; combined with the reused (persisted) prefix, re-runs
  converge on the same resources.

### CLI (`dsf new`, extended)

New arguments:

- `--name-prefix` **(required)** — base prefix (validated, sanitized, then
  randomized into the effective prefix).
- `--environment` (default `dev`), `--location` (default `swedencentral`),
  `--workload-principal-id` (default `""`).

`_cmd_new` resolves the effective prefix idempotently: if
`config/instances/<product>.json` already exists, **reuse** its
`spec.name_prefix`; otherwise call `make_name_prefix(args.name_prefix)`.

> **SP1 test impact:** the three `tests/instance/test_cli_new.py` cases now pass
> `--name-prefix`, and a new case asserts `--name-prefix` is required. SP1's
> provisioner tests are unaffected (they construct `InstanceSpec` directly, where
> `name_prefix` defaults to `"dsf"`).

## Data flow

```
dsf new --product P --owner O --name-prefix B --execute
   │
   ├─ resolve effective prefix:  existing manifest? reuse : make_name_prefix(B)
   ├─ InstanceProvisioner(spec).apply(execute=True)
   │     create_repo / squad_init / squad_copilot      → gh / squad
   │     create_resource_group                          → az group create
   │     provision_azure                                → az deployment group create
   │            └─ capture properties.outputs (JSON)    → AzureProvisionResult
   │     write_config                                   → manifest (incl. azure)
   └─ config/instances/P.json  ← persisted prefix + Azure outputs
```

## Testing strategy

All offline via the injected `run` (a `fake_run` recording calls / returning canned
JSON); no real `az`/network.

- **Naming:** `make_name_prefix` sanitizes, substrings to ≤8 + 4 token chars,
  starts with a letter, total ≤12; rejects empty/invalid bases; deterministic with
  injected `token`.
- **Spec:** new fields + defaults; `name_prefix` validation accepts/rejects.
- **Plan:** 8 steps in order; `create_resource_group` + `provision_azure` active;
  deferred set is exactly `{deploy_council, deploy_sre}`; `provision_azure` command
  shape (deployment verbs, `-f …/infra/main.bicep`, the four `-p` params,
  `--query properties.outputs`).
- **Apply:** `execute=True` runs both az steps, never touches deferred subsystems,
  and parses outputs into `manifest.azure`; `execute=False` leaves `azure=None`.
- **CLI:** `--name-prefix` required (missing ⇒ SystemExit); flags thread into the
  spec/plan; effective prefix is stable across two `--write-plan` runs (idempotency).

## Out of scope (later sub-projects)

- Consuming `manifest.azure` outputs in `build_services('azure')` — **SP3**.
- Real homelab workload-principal wiring / Entra SP creation beyond the passthrough.
- `az deployment group what-if` preview gating (single-stage chosen).
- Destroy / teardown / cost cleanup — **SP7** (`dsf destroy`).

## Risks / notes

- **Manifest is the idempotency source of truth.** If `config/instances/<product>.json`
  is absent on a machine where the RG already exists, a fresh prefix would be
  generated and orphan the prior resources. Mitigation: commit instance manifests
  to the dsf repo (matches the charter's "single source of truth" registry model).
- **Cost.** `--execute` now spends real money; the dry-run default and the explicit
  `--execute` opt-in remain the guardrails (consistent with the RUNBOOK warning).
- ADR 0002 holds: Bicep provisions backing services only; no compute is deployed
  here, and `workloadPrincipalId` stays a passthrough until SP3.
- **Network access stays at the Bicep default** (`allowPublicNetworkAccess=false`,
  i.e. public access Denied). SP2 passes only `namePrefix`, `environmentName`,
  `location`, and `workloadPrincipalId`; whether the homelab runtime needs public
  access (or private endpoints) to reach these services is resolved in **SP3**
  alongside `build_services('azure')`.
