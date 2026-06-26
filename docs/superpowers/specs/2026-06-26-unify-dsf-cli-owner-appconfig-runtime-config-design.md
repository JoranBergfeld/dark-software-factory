# Unify the `dsf`/`dsfctl` CLIs + owner App Config runtime-config index

## Problem

Running a one-shot sweep against a freshly-provisioned product is awkward in two
ways that both stem from a missing config-resolution path:

1. **Asymmetric CLIs.** Provisioning is `dsf new --product X` (the `dsf-cli`
   member, console script `dsf`). Operating it is `dsfctl sweep` (the
   `dsf-feature-council` member, console script `dsfctl`). There is no
   `dsfctl sweep --product X`: the runtime scopes itself from `DSF_PRODUCT`, not
   a flag. Two binaries with an unexplained split is hard to teach and to
   maintain.
2. **A wall of env vars.** `dsfctl sweep` calls `build_services()`, which
   **requires** `DSF_PRODUCT` plus every Azure data-plane endpoint
   (`AZURE_APPCONFIG_ENDPOINT`, `AZURE_COSMOS_ENDPOINT`, `AZURE_OPENAI_ENDPOINT`,
   `AZURE_OPENAI_DEPLOYMENT`, `AZURE_OPENAI_EMBEDDING_DEPLOYMENT`, …) and raises
   if any is unset (ADR 0014: no fallback). An operator must hand-export all of
   them to run a local sweep, even though `dsf new` already knows every value.

The deployed runtime does **not** have this problem: `infra/main.bicep` injects
`DSF_PRODUCT` and every endpoint as container env (lines 427-434) and reads
secrets from Key Vault via the Container App's managed identity. So the env wall
is purely a **local operator / CI** concern — the running container already
self-configures.

## Goals

- `dsf sweep --product X` (and `run`, `serve-orchestrator`) works with **zero
  hand-exported endpoint env vars**, symmetric with `dsf new --product X`.
- **One operator binary**: fold the `dsfctl` runtime commands into `dsf`; remove
  the `dsfctl` console script.
- Resolve a product's runtime config from Azure (no dependence on local
  `config/instances/` files), so it works for any operator/CI with the right
  RBAC.
- Keep the deployed ACA runtime untouched and keep all four import-linter
  contracts green (`core` clean; `cli` and `feature-council` still must not
  import each other).

## Non-goals

- Changing how **secrets** are resolved. The GitHub App private key and the
  WebIQ key stay in the product Key Vault and are read at runtime via
  `DefaultAzureCredential`. They never enter the index.
- Changing the deployed ACA runtime's configuration path (Bicep env injection).
- Reworking `dsf charter`, which already self-resolves endpoints from the local
  manifest. (It *may* later adopt the shared resolver; out of scope here.)
- A durable push/inbox queue. DSF stays pull-only.

## Approach

Two Azure App Configuration stores, kept conceptually distinct:

- **Product App Config** — one per product, in `rg-dsf-<product>` (exists today).
  Holds council flags/weights; read by the runtime as the `ConfigStore` port.
  **Unchanged.**
- **Owner App Config** — one per owner, in the owner RG `rg-dsf-app` (**new**).
  A *runtime-config index*: every product's bootstrap env stored under App
  Configuration **label = product**.

`dsf new` publishes a product's full runtime env to the owner index. The runtime
resolves it from just `--product` plus a single pointer
(`DSF_OWNER_APPCONFIG_ENDPOINT`). `dsf` gains thin front-door subcommands that
**subprocess** the feature-council runtime module, so cli never imports
feature-council.

### Why an owner App Config index (vs alternatives)

- **ARM deployment-output discovery** (read `az deployment group show`): no new
  infra, but more `az` plumbing and couples to deployment retention. Rejected in
  favor of an explicit, queryable config home.
- **Owner Key Vault index**: reuses the existing owner pointer, but the tenant's
  MG policy forces a ≤30-day expiry on every secret, so sweeps would break a
  month after provisioning. Rejected.
- **Owner App Configuration**: the right tool for config — native per-scope
  **labels**, no expiry policy, RBAC data-plane auth. **Chosen.**

## Components

Respecting `[tool.importlinter]`: the shared resolver lives in `core`; `cli` and
`feature-council` each use it without importing one another.

### core — `dsf/config/owner_index.py` (new)

Built on the existing `ConfigGateway` seam (`get`/`set`/`list` over
`(key, label)`; see `core/src/dsf/config/azure_store.py`) so it is testable
offline with an in-memory gateway. Values are stored as **plain strings** (these
are env values, not JSON-encoded flags).

- `publish_runtime_config(endpoint, product, values: Mapping[str, str], *, gateway=None) -> None`
  — upsert each key with `label=product`.
- `read_runtime_config(endpoint, product, *, gateway=None) -> dict[str, str]`
  — return every key stored under `label=product` as an env dict.
- `delete_runtime_config(endpoint, product, *, gateway=None) -> None`
  — remove the product's labelled keys (teardown).
- `runtime_env_for_product(product, *, owner_endpoint=None, base_env=None) -> dict[str, str]`
  — resolve `owner_endpoint` from `DSF_OWNER_APPCONFIG_ENDPOINT` when not given,
  read the index, then layer `os.environ` and force `DSF_PRODUCT=product`.
  Precedence (mirrors `dsf charter._base_env`): **index < exported env < forced
  `--product`** — an explicitly exported var always wins. A missing pointer or
  unreadable index yields just `{**os.environ, "DSF_PRODUCT": product}` (the
  operator can still export endpoints by hand).

### feature-council — `dsf/runtime/control.py`

- Add `--product` to `run`, `sweep`, and `serve-orchestrator`.
- A `--product`-aware `_get_services(args)`: when `--product X` is set, build
  with `build_services(env=runtime_env_for_product(X))`; when omitted, the
  current pure-`os.environ` path is used unchanged (this is the deployed
  container's path — it sets `DSF_PRODUCT` + endpoints as env and passes no
  flag).
- The module stays runnable as `python -m dsf.runtime.control`. The `dsfctl`
  **console script is removed** (see Migration).

### cli — `dsf/cli/factory.py`

- New front-door subcommands `run`, `sweep`, `serve-orchestrator`,
  `serve-agent`. Each **re-declares its small arg set** (so `dsf sweep --help` is
  informative) including `--product`, then forwards to the runtime via a single
  injectable `runner` (default `subprocess.run`):
  `[sys.executable, "-m", "dsf.runtime.control", <cmd>, *args]`, inheriting the
  process environment, and returns the child's exit code. **No feature-council
  import.**
- `dsf new`: after Azure outputs are captured (next to `render_runtime_bundle`),
  **publish** the product's runtime env to the owner index under `label=product`.
  Best-effort: if `DSF_OWNER_APPCONFIG_ENDPOINT` is unset, warn and skip — never
  fail `dsf new` (same posture as the post-provision charter hint).
- `dsf delete` / `dsf offboard`: add an idempotent **remove index entry** step,
  mirroring `render_product_unregistration`.
- `dsf bootstrap`: create the owner App Config and grant the operator data-plane
  access (see Bootstrap).

## Data model — index keys (label = product)

Everything `dsf new` already holds (manifest `azure.outputs` + `github_app`
binding + the static runtime knobs):

| Key | Source |
| --- | --- |
| `AZURE_APPCONFIG_ENDPOINT` | outputs `appConfigEndpoint` (the **product** App Config) |
| `AZURE_KEYVAULT_URI` | outputs `keyVaultUri` |
| `APPLICATIONINSIGHTS_CONNECTION_STRING` | outputs `appInsightsConnectionString` |
| `AZURE_COSMOS_ENDPOINT` | outputs `cosmosEndpoint` |
| `AZURE_OPENAI_ENDPOINT` | outputs `openaiEndpoint` |
| `AZURE_OPENAI_DEPLOYMENT` | outputs `openaiDeployment` |
| `AZURE_OPENAI_EMBEDDING_DEPLOYMENT` | outputs `openaiEmbeddingDeployment` |
| `WEBIQ_PROVIDER` | static (`webiq`) |
| `WEBIQ_API_KEY_SECRET` | static (`webiq-api-key`) |
| `GITHUB_APP_ID` | manifest `github_app.app_id` |
| `GITHUB_INSTALLATION_ID` | manifest `github_app.installation_id` |
| `GITHUB_APP_PRIVATE_KEY_SECRET` | manifest `github_app.private_key_secret` |
| `GITHUB_REPOSITORY` | spec `github_repo()` |

`DSF_PRODUCT` is implied by the label and forced by `--product`; storing it is
harmless but unnecessary. **No secret values** are stored — only the *name* of
the private-key secret. `AZURE_APPCONFIG_ENDPOINT` in the index points at the
**product** App Config, which the runtime then reads for flags.

Reuse `runtime_endpoint_env` (`cli/instance/runtime_render.py`) — already the
single source of truth mapping outputs → `AZURE_*` env — to assemble the
endpoint subset, so the index and the rendered `.env.orchestrator` never drift.

## The single owner pointer

`DSF_OWNER_APPCONFIG_ENDPOINT` — printed by `dsf bootstrap`, exported by the
operator exactly like the existing `DSF_OWNER_KEYVAULT_URI`. Both the `dsf`
front-door and the runtime module read it from the inherited environment (the
front-door subprocess inherits `os.environ`, so it is passed through
automatically).

## Bootstrap / RBAC

Extend `dsf bootstrap` (`cli/instance/app_bootstrap.py`):

- Create the owner App Config in the owner RG (new `--appconfig-name`, default
  derived from `--keyvault-name`; **Standard** SKU; `--enable-rbac-authorization`
  / Microsoft Entra auth). Grant the operator **App Configuration Data Owner** on
  the store (publish on `dsf new`, read on `dsf sweep`).
- Reuse the existing data-plane RBAC-propagation **retry** (the owner-KV secret
  seed already retries the initial 403s) so the first publish/read after the
  grant does not fail.
- Keep the command builders **pure** (`owner_appconfig_ensure_commands`, like
  `owner_kv_ensure_commands`) for unit testing.
- `BootstrapConfig` / `BootstrapResult` gain the App Config name + endpoint;
  `_cmd_bootstrap` prints
  `export DSF_OWNER_APPCONFIG_ENDPOINT=<endpoint>`.

The deployed ACA managed identity gets **no** new grant — the index is operator/
CI-only; the container has its env from Bicep.

## Data flow

1. `dsf bootstrap` → owner KV (today) **+ owner App Config (new)** + RBAC; prints
   `DSF_OWNER_KEYVAULT_URI` and `DSF_OWNER_APPCONFIG_ENDPOINT`.
2. Operator exports both (one time).
3. `dsf new --product X` → provisions `rg-dsf-X`, captures outputs, renders the
   local bundle, **publishes `label=X`** to the owner index.
4. `dsf sweep --product X` → subprocess
   `python -m dsf.runtime.control sweep --product X` (inherits env, incl. the
   pointer).
5. runtime `sweep --product X` → `runtime_env_for_product(X)` →
   `build_services(env=…)` → `run_sweep`. Secrets read from the product Key Vault
   via `DefaultAzureCredential` (unchanged).
6. `dsf delete X` / `dsf offboard X` → removes `label=X` from the index.

## Testing (offline, deterministic — ADR 0014)

- **core `owner_index`**: publish → read → delete round-trip; label scoping
  (product A never sees product B); `runtime_env_for_product` precedence
  (index < env < forced `DSF_PRODUCT`); missing-pointer degradation. In-memory
  `ConfigGateway`.
- **feature-council control**: `--product` path resolves via an injected/seamed
  resolver and calls `build_services(env=…)`; the no-`--product` path is
  unchanged.
- **cli front-door**: injected `runner` asserts argv
  `[sys.executable, "-m", "dsf.runtime.control", "sweep", "--product", "X"]` and
  that the child exit code is propagated; no real subprocess.
- **cli `new` / `offboard` / `bootstrap`**: publish/delete invoked with the right
  values (seamed gateway); new `owner_appconfig_*` command builders unit-tested;
  `BootstrapResult` carries the endpoint.
- **Gates**: `uv run ruff check .`, `uv run lint-imports` (**must stay 4 kept /
  0 broken**), `uv run pytest -q`.

## Migration / blast radius

- `feature-council/pyproject.toml`: remove the `dsfctl` console script.
- `feature-council/src/dsf/runtime/Dockerfile`: `CMD` →
  `["python", "-m", "dsf.runtime.control", "serve-orchestrator", "--loop"]`;
  update `feature-council/tests/runtime/test_runtime_image.py`.
- Invert `cli/tests/cli/test_factory.py` (the factory now *does* expose runtime
  ops); extend `cli/tests/cli/test_control.py` for the `--product` path.
- Docs: `docs/site/get-started/operate.md`, provision docs, and the
  factory user-facing strings (`dsfctl sweep` → `dsf sweep`); add
  `DSF_OWNER_APPCONFIG_ENDPOINT` to `.env.example`.

## Risks / open considerations

- **Two App Configs** (owner *index* vs product *flags*) — documented clearly to
  avoid confusion; `AZURE_APPCONFIG_ENDPOINT` in the index is the *product* store.
- **Runtime must be installed** in the env that runs `dsf` (so `python -m
  dsf.runtime.control` resolves). True in the `uv` workspace; documented as an
  assumption of the unified CLI.
- **RBAC propagation delay** on the new owner App Config — absorbed by the
  existing retry pattern.
- **Standard SKU** owner App Config — negligible cost; required for labels +
  RBAC at scale.
- **Front-door arg duplication** — the `dsf` front-door re-declares the runtime
  arg sets rather than importing them (contract boundary). They are small; a
  drift check is optional.
