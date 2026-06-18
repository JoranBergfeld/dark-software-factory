# SP3b — Real Azure data adapters behind the `azure` seam

- Status: Design (awaiting/under implementation)
- Date: 2026-06-18
- Charter row: SP3b ("Real Azure data adapters — replace the in-memory impls
  behind the azure seam with real App Configuration, Cosmos, and LLM adapters;
  App Configuration first").
- Predecessors: ADR 0001 (ports + offline-by-default), ADR 0004 (ACA runtime on
  a user-assigned MI), ADR 0005 (honest local implementations).

## 1. Goal

`build_services('azure')` currently wires a real `RealGitHubClient` + the
OpenTelemetry tracer, but keeps **config, memory, and model** on the in-memory
implementations (`InMemoryConfigStore`, `InMemoryMemoryStore`,
`DeterministicModelClient`) behind a deferred-adapter seam. SP3b lands the three
real Azure adapters behind the **same ports**, with **no change to any caller**
and **no loss of the offline/dry-run/eval posture**.

Increment order (each its own commit, TDD, verified green before the next):

1. **App Configuration** → `AppConfigStore` (`dsf.config`)
2. **Cosmos DB** → `CosmosMemoryStore` (`dsf.memory`)
3. **Azure OpenAI** → `AzureOpenAIModelClient` (`dsf.model`)

## 2. Design pattern (mirrors `RealGitHubClient`)

Every adapter:

- Implements the existing `typing.Protocol` port unchanged.
- Takes the **SDK client as an injected constructor parameter** (`client=None`).
  When `None`, it lazily builds a real SDK client from an endpoint +
  `DefaultAzureCredential`. **Tests inject an in-memory SDK double → 100%
  offline**, exactly like `RealGitHubClient(_run=...)`.
- **Lazy-imports its Azure SDK inside the builder**, never at module top level —
  mirroring `dsf.observability.tracing.build_tracer`, which degrades to the
  `NoOpTracer` when OpenTelemetry is absent. So importing `dsf.config.azure_store`
  does **not** require `azure-appconfiguration` to be installed; only *building a
  real client* does. Missing-SDK raises a clear `RuntimeError` naming the extra.

### Module placement (domain-co-located, per ADR 0005)

Each real adapter lives in a focused module **beside** its in-memory sibling:

| Port | In-memory impl | Real adapter (new) |
|---|---|---|
| `ConfigStore` | `dsf/config/store.py` `InMemoryConfigStore` | `dsf/config/azure_store.py` `AppConfigStore` |
| `MemoryStore` | `dsf/memory/store.py` `InMemoryMemoryStore` | `dsf/memory/azure_store.py` `CosmosMemoryStore` |
| `ModelClient` | `dsf/model/client.py` `DeterministicModelClient` | `dsf/model/azure_client.py` `AzureOpenAIModelClient` |

### Optional dependencies

Add an **`azure` extra** to `pyproject.toml`; local/CI/eval never install it
(tests inject doubles, adapters lazy-import):

```toml
[project.optional-dependencies]
azure = [
  "azure-appconfiguration>=1.5",
  "azure-cosmos>=4.5",
  "azure-identity>=1.15",
  "openai>=1.30",
]
```

## 3. Adapter 1 — `AppConfigStore` (Azure App Configuration)

The `ConfigStore` port is **sync**, so use the sync
`azure.appconfiguration.AzureAppConfigurationClient`.

**Mapping (must observably match `InMemoryConfigStore`).** Today the in-memory
store resolves namespaced flags onto nested seed keys. Extract that mapping into
a shared `resolve_flag_key(flag) -> str | None` in `dsf/config/store.py`, used by
**both** stores so they cannot drift:

- `dry_run` → `dry_run`
- `critic.<name>` → `critics.<name>.enabled`
- `agent.<KIND>` → `agents.<KIND>.enabled`
- `trigger.<KIND>.paused` → `triggers.<KIND>.paused`
- otherwise → `None` (unknown flag ⇒ `is_enabled` returns `False`)

`InMemoryConfigStore.is_enabled` is refactored to call `resolve_flag_key`
(behavior-preserving; covered by existing `tests/config/test_store.py`).

**App Configuration semantics.** Config is stored as **flat dotted keys** (key =
dotted path, value = JSON). Per-product overrides use App Configuration's native
**label** (label = product); a labelled setting takes precedence over the
unlabelled one.

- `get_value(key, default)`: `get_configuration_setting(key)` (no label) → JSON
  parse; `default` if absent.
- `is_enabled(flag, product)`: `k = resolve_flag_key(flag)`; if `None` → `False`.
  Read `k` with `label=product`, falling back to no label; JSON-parse bool;
  `False` if absent.
- `set_flag(flag, value, product)`: `k = resolve_flag_key(flag)`;
  `set_configuration_setting(key=k, value=json(bool), label=product or None)`.
- `snapshot()`: `list_configuration_settings()` → assemble nested dict + an
  `_overrides` view of labelled settings (parity with the in-memory snapshot
  shape).

**Test double:** `InMemoryAppConfigClient` (in `tests/support/azure_doubles.py`)
holds `{(key, label): value}` and implements only
`get_configuration_setting` / `set_configuration_setting` /
`list_configuration_settings`.

## 4. Adapter 2 — `CosmosMemoryStore` (Azure Cosmos DB)

The `MemoryStore` port is **async**, so use `azure.cosmos.aio.CosmosClient`.
One database per product; three containers:

| Tier | Container | Partition key | Notes |
|---|---|---|---|
| working | `working` | `/key` | per-item Cosmos **TTL** (`ttl` seconds); `get_working` reads by id, expired items auto-removed server-side |
| records | `records` | `/kind` | `query_similar` runs `SELECT * FROM c WHERE c.kind=@kind`, then ranks client-side by **token-overlap** (reuse `_tokens`/`_overlap` from `dsf.memory.store`), returns top `k`; per-item TTL honored |
| lessons | `lessons` | `/product` | `get_lessons` queries `product=@product`, newest first, top `k` |

**Vector search is deferred** — keep token-overlap ranking for parity with
`InMemoryMemoryStore` (ADR 0001 §4 anticipated Cosmos native vectors; revisit
later). The adapter's job here is the port translation, not new ranking.

**TTL:** the adapter translates `ttl` → the Cosmos item `ttl` field; expiry
enforcement is Cosmos's responsibility (tests assert the adapter *sets* `ttl`,
not that items expire).

**Test double:** `InMemoryCosmosClient` (in `tests/support/azure_doubles.py`)
exposing the narrow async surface used: `get_database_client(db)
.get_container_client(name)` → `upsert_item` / `read_item` /
`query_items` (async-iterable), backed by dicts; implements only the `=`-filter
query subset the adapter issues.

## 5. Adapter 3 — `AzureOpenAIModelClient` (Azure OpenAI)

The `ModelClient` port is **async**: `complete(system, prompt, schema=None)`.
Use `openai.AsyncAzureOpenAI` with `AZURE_OPENAI_ENDPOINT`, a deployment name,
and an `azure_ad_token_provider` from `DefaultAzureCredential`.

- No `schema` → return `choices[0].message.content` (a `str`).
- With `schema` → call with a `json_schema` response format built from
  `schema.model_json_schema()`, then `schema.model_validate_json(content)`.

The synthesizer's `[deterministic]` echo sentinel
(`dsf.council.synthesizer._prose_or_fallback`) is **only** produced by
`DeterministicModelClient`. The real client returns real prose/JSON, so the
fallback simply never triggers — **no coupling change needed**.

**Test double:** `RecordingOpenAIClient` (in `tests/support/azure_doubles.py`)
records messages and returns a canned completion (a JSON string for the
structured-output test, plain prose otherwise).

## 6. Wiring into `build_services('azure')` — graceful per-endpoint degradation

Each adapter lights up **only when its endpoint is configured**, else falls back
to the in-memory sibling. This keeps `azure` mode runnable mid-rollout and keeps
existing container tests (which set only `DSF_PRODUCT`) green.

Extend `AzureRuntimeSettings` with `openai_endpoint` (`AZURE_OPENAI_ENDPOINT`)
and `openai_deployment` (`AZURE_OPENAI_DEPLOYMENT`). Then in the azure branch:

```text
config = AppConfigStore(endpoint=settings.appconfig_endpoint)
         if settings.appconfig_endpoint else InMemoryConfigStore.from_defaults()
memory = CosmosMemoryStore(endpoint=settings.cosmos_endpoint, database=settings.product)
         if settings.cosmos_endpoint else InMemoryMemoryStore()
model  = AzureOpenAIModelClient(endpoint=settings.openai_endpoint,
                                deployment=settings.openai_deployment)
         if (settings.openai_endpoint and settings.openai_deployment)
         else DeterministicModelClient()
```

## 7. Testing strategy

- **Per-adapter unit tests** (`tests/config/`, `tests/memory/`, `tests/model/`)
  using the injected in-memory SDK doubles in `tests/support/azure_doubles.py`.
  Cover: the read/write/translate semantics, the flag→key mapping, TTL field
  translation, structured-output parsing.
- **Import-safety test:** importing each `azure_*` adapter module succeeds with
  **no Azure SDK installed** (asserts the lazy-import discipline).
- **Container tests:** with endpoints set in `env`, `build_services('azure')`
  wires the real adapter type (`isinstance`); with endpoints unset, falls back to
  the in-memory sibling.
- **Unchanged:** `local` mode, the eval gate (`build_services('local')`), and the
  dry-run line. No integration tests against live Azure (billable; out of scope —
  adapters are unit-tested via injected doubles).

## 8. ADR

Add **ADR 0006 — Azure data adapters: injected SDK clients + optional extra +
graceful degradation**, recording: real adapters behind the existing ports;
SDK client injected (offline tests); `azure` optional extra with lazy imports;
per-endpoint graceful fallback to the in-memory sibling. Fulfills (does not
supersede) ADR 0001/0005.

## 9. Out of scope

- Cosmos **vector** retrieval (token-overlap retained for now).
- App Configuration **feature-flag content-type** schema (plain boolean
  key-values are sufficient and match the in-memory model).
- Live-Azure integration tests / provisioning of the data resources (covered by
  SP2 Bicep; this SP is the adapter code + offline tests only).
