# WebIQ source agent on the real WebIQ SDK (retire Foundry Grounding-with-Bing)

## Problem

The `webiq` source agent does external/industry web research. Its default
provider is Azure AI Foundry's *Grounding with Bing Search* tool
(`WEBIQ_PROVIDER=foundry`), which requires a Foundry **connection** resource
(`Microsoft.CognitiveServices/.../connections`, category
`GroundingWithBingSearch`) provisioned per product.

Provisioning that connection is unreliable. On a brand-new Foundry account the
connection's API-key secret write races the account-RP's async managed-Key-Vault
registration: the inline ARM connection 500s for ~10 min and never re-drives, and
the out-of-band retry (commit `e6a8d16`) still failed to beat the race in repeated
live runs (`pet-clinic3`, `pet-clinic4`). The whole Bing-grounding path is a
recurring deployment hazard with no reliable fix.

Meanwhile, **WebIQ** — the Microsoft capability announced at Build — is a real,
first-class product with its own SDK (`pip install webiq`) and simple API-key
auth. The source agent is already *named* `webiq`; it should research through the
real WebIQ SDK instead of the Foundry/Bing connection.

## Goals

- The `webiq` agent researches via the real WebIQ SDK (`WebIQAsyncClient`),
  authenticated with an API key.
- The WebIQ API key is supplied once into the **central/owner** Key Vault and
  seeded into each product's Key Vault by `dsf new`, exactly like the existing
  `github-app-private-key` flow. The runtime reads it from the product vault via
  its managed identity.
- `WEBIQ_PROVIDER` defaults to `webiq`; `tavily` stays selectable.
- The Foundry Grounding-with-Bing path is fully removed (provider, Bicep
  resources, out-of-band provisioning step, opt-out flag).
- All `src/` code is real (ADR-0014); the SDK call path is unit-tested with an
  injected `httpx` transport — no network.

## Non-goals

- Keeping the `foundry` / Bing-grounding provider as an option. It is deleted.
- Exposing WebIQ's other surfaces (videos / news / images / browse / classic).
  Only `web.search` is wired; the others are out of scope (YAGNI).
- An interactive `dsf bootstrap` sub-flow to *write* the central secret. The
  operator places `webiq-api-key` in the owner vault out-of-band (same as
  `github-app-private-key` today). `dsf new` only seeds owner -> product.
- ACA Key Vault secret references. The key is seeded **after** the ARM deploy
  (the product vault is created by that deploy), so a deploy-time secret
  reference would point at a non-existent secret. App-side KV reads (the
  established GitHub-App-key pattern) are used instead.

## Verified SDK facts (`webiq==0.1.0`)

- `WebIQAsyncClient(*, api_key=..., http_client: httpx.AsyncClient | None=...)`,
  `base_url` default `https://api.microsoft.ai/v3`, async, with `aclose()`.
- `await client.web.search(query, *, max_results=None, content_format=None, ...)
  -> WebResponse`. The transport issues `POST {base_url}/search/web`. When an
  `http_client` is injected it is used as-is (no base_url override), so tests
  inject `httpx.AsyncClient(base_url="https://api.microsoft.ai/v3",
  transport=httpx.MockTransport(handler))`.
- `WebResponse.webResults: list[WebResult]`; `WebResult` fields: `title`, `url`,
  `content`, `lastUpdatedAt`, `crawledAt`, `language`, `isAdult`.
- Lightweight deps: `httpx`, `pydantic`, `anyio` (no Azure extra needed).

## Design

### A. Agent: WebIQ provider (`feature-council/src/dsf/agents/webiq/`)

`client.py` — `build_webiq_client_from_env(...)` returns the same async
`search(query) -> list[dict]` callable the backend already consumes
(`{finding, url, confidence}`):

- `WEBIQ_PROVIDER` (default **`webiq`**) selects the backend:
  - `webiq` -> new WebIQ-SDK search (below).
  - `tavily` -> unchanged Tavily path.
  - any other value (incl. the removed `foundry`/`azure`/`bing`) raises
    `NotImplementedError` naming the supported values.
- New `_build_webiq_search` in a new module `webiq_sdk.py` (mirroring how the
  removed `foundry.py` isolated its backend; keeps `client.py` thin):
  - **Key resolution:** `WEBIQ_API_KEY` env if set (local/dev override); else read
    product-KV secret named by `WEBIQ_API_KEY_SECRET` (default `webiq-api-key`)
    from `AZURE_KEYVAULT_URI` via an injectable `key_reader(uri, name) -> str`
    (default = the real KV read used by the GitHub App client). Raises a clear
    error if neither is available.
  - **Search:** build `WebIQAsyncClient(api_key=key, http_client=injected)`; per
    query `await client.web.search(query, max_results=WEBIQ_MAX_RESULTS)`;
    `aclose()` in a `finally`.
  - **Mapping** (`WebResult` -> dict): `finding = r.content or r.title or ""`,
    `url = r.url`, `confidence = 0.6` (the API returns no per-result score; same
    constant the old Bing path used). Skip rows with neither finding nor url. Cap
    at `WEBIQ_MAX_RESULTS` (default 5; reuse the existing coercion helper).
- Delete `foundry.py` and its tests. `backend.py` / `main.py` are unchanged
  except doc strings (the injected `search` contract is identical).

### B. Provisioner: seed the WebIQ key (`cli/src/dsf/instance/provisioner.py`)

- New step `seed_webiq_key` (placed next to `seed_app_key`, after
  `provision_azure`). `_seed_webiq_key` mirrors `_seed_app_key`: read
  `webiq-api-key` from the owner vault (`az keyvault secret show`), write it into
  the product vault (`az keyvault secret set`) with the existing
  `_SEED_*` retry (the Secrets-User grant vs data-plane write race). Value is
  materialized to a `0600` temp file and unlinked; never echoed in argv.
  Requires `_owner_keyvault_uri` (clear error otherwise, like `_seed_app_key`).
  Both `_seed_webiq_key` and `_seed_app_key` set `--content-type text/plain` and
  a 30-day `--expires` to satisfy the management-group Key Vault policy that denies
  secret writes lacking a content type or expiry; the key is re-seeded each
  `dsf new` (and must be re-seeded on rotation/expiry).
- **Remove** the Bing path: the `connect_bing_grounding` step,
  `_connect_bing_grounding`, `_is_transient_bing_error`,
  `_TRANSIENT_BING_STATUS`/`_TRANSIENT_BING_TOKENS`,
  `_BING_CONNECT_MAX_ATTEMPTS`/`_BING_CONNECT_RETRY_DELAY`, the
  `enable_bing_grounding` spec field, and the `enableBingGrounding=...` deploy
  argument. (This supersedes commits `503e328` and `e6a8d16`, and reverts the
  `enable_bing_grounding` half of the deploy-hardening work; the poller timeout
  from that work stays.)

### C. Infra (`infra/main.bicep`)

- Remove the `Microsoft.Bing/accounts` resource, its role assignment(s), the
  `bingConnectionResourceId` var, the `bingConnectionId` / `bingAccountId` /
  `bingAccountEndpoint` outputs, the `enableBingGrounding` parameter, the Foundry
  **project** (`Microsoft.CognitiveServices/accounts/projects` — the project is
  Bing-only, so it is removed along with the Bing account), and the
  `AZURE_AI_PROJECT_ENDPOINT` container env and output. The Foundry **account**
  (Azure OpenAI for chat + embeddings) and its model deployments remain.
- Container app `env`: drop `WEBIQ_BING_CONNECTION_ID`; change `WEBIQ_PROVIDER`
  from `'foundry'` to `'webiq'`; add `{ name: 'WEBIQ_API_KEY_SECRET', value:
  'webiq-api-key' }`. Keep `AZURE_KEYVAULT_URI` (already present).
- `cli/src/dsf/instance/runtime_render.py`: drop the
  `("WEBIQ_BING_CONNECTION_ID", "bingConnectionId")` entry from `_ENDPOINT_MAP`.
  Add static `WEBIQ_PROVIDER=webiq` and `WEBIQ_API_KEY_SECRET=webiq-api-key`
  lines to the rendered `.env.orchestrator` for fidelity with the container env.

### D. Dependency

- `uv add webiq` into `feature-council` (`feature-council/pyproject.toml`
  `dependencies`), refresh `uv.lock`. The webiq Dockerfile pip-installs
  `./feature-council`, so the runtime image picks `webiq` up with no Dockerfile
  change.

## Testing

- **WebIQ client** (`feature-council/tests/agents/webiq/`): inject
  `httpx.AsyncClient(base_url="https://api.microsoft.ai/v3",
  transport=httpx.MockTransport(handler))` and a stub `key_reader`. Assert the
  real SDK posts `/search/web`, and that a canned `{webResults: [...]}` maps to
  the expected `{finding, url, confidence}` list (content-vs-title fallback,
  empty-row skip, `max_results` cap). Assert `WEBIQ_PROVIDER=foundry` now raises.
  Keep/adjust the Tavily tests. Delete the foundry tests.
- **Provisioner** (`cli/tests/instance/test_provisioner.py`): `seed_webiq_key`
  reads owner -> writes product (retry on transient failure, key never in argv,
  clear error when no owner vault). Remove the bing-connect and
  `enable_bing_grounding` tests.
- **Bicep structural tests**: no `Microsoft.Bing/accounts`, no
  `WEBIQ_BING_CONNECTION_ID`, no `enableBingGrounding` param; container env has
  `WEBIQ_PROVIDER=webiq` and `WEBIQ_API_KEY_SECRET=webiq-api-key`.
- Gates (repo root, all must pass): `uv run ruff check .`,
  `uv run lint-imports` (4 kept, 0 broken), `uv run pytest -q`, and
  `az bicep build --file infra/main.bicep --stdout > /dev/null`.

## Docs / ADR

- Fix `feature-council/src/dsf/agents/webiq/README.md` (stale: claims
  "only `tavily` is supported") -> WebIQ default, Tavily optional, env table
  (`WEBIQ_PROVIDER`, `WEBIQ_API_KEY` / `WEBIQ_API_KEY_SECRET`, `WEBIQ_MAX_RESULTS`,
  `TAVILY_API_KEY`).
- Update `docs/site/get-started/provision-a-factory.md` and
  `docs/site/get-started/operate.md`: replace Bing-connection notes with the
  central-vault WebIQ-key setup (place `webiq-api-key` in the owner vault; it is
  seeded per product).
- New ADR `docs/adr/0020-webiq-via-webiq-sdk.md`: decision to research via the
  WebIQ SDK and retire Foundry Grounding-with-Bing; supersede the relevant part
  of the deploy-hardening note.

## Rollout / commit hygiene

- Conventional Commits; co-authored trailer. Stay on `main`, do not push unless
  asked.
- The change set effectively retires the Bing work (`503e328`, `e6a8d16`, and the
  `enable_bing_grounding` part of the deploy-hardening commit). Net result is a
  forward change (remove + replace), not a history rewrite.
