# SP3b — Azure data adapters Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Land real Azure adapters (App Configuration, Cosmos, Azure OpenAI) behind the existing `ConfigStore`/`MemoryStore`/`ModelClient` ports, wired into `build_services('azure')` with per-endpoint graceful fallback, fully unit-tested offline.

**Architecture:** Each adapter talks to a **narrow gateway** seam (a tiny `Protocol` with 1-3 methods) rather than the raw Azure SDK. The default gateway lazily wraps the SDK (built from an endpoint + `DefaultAzureCredential`); tests inject a dict-backed in-memory gateway. SDKs are an optional `azure` extra and are never imported at module top level — exactly mirroring how `RealGitHubClient` injects `_run`.

**Tech Stack:** Python 3.12, uv, pytest (`asyncio_mode=auto`), ruff. Optional: `azure-appconfiguration`, `azure-cosmos`, `azure-identity`, `openai`.

**Verification commands (run from repo root):**
- Lint: `uv run ruff check .`
- Tests (all): `uv run pytest -q`
- One test: `uv run pytest tests/config/test_azure_store.py -q`
- Eval gate (must stay PASSED): `uv run python -m dsf.evals.runner --gate`

---

## File Structure

**Create:**
- `tests/support/azure_doubles.py` — in-memory gateway doubles (`InMemoryConfigGateway`, `InMemoryCosmosGateway`, `RecordingChatGateway`).
- `src/dsf/config/azure_store.py` — `AppConfigStore` + `ConfigGateway` + `_SdkConfigGateway`.
- `src/dsf/memory/azure_store.py` — `CosmosMemoryStore` + `CosmosGateway` + `_SdkCosmosGateway`.
- `src/dsf/model/azure_client.py` — `AzureOpenAIModelClient` + `ChatGateway` + `_SdkChatGateway`.
- `tests/config/test_azure_store.py`, `tests/memory/test_azure_store.py`, `tests/model/test_azure_client.py`.
- `docs/adr/0006-azure-data-adapters.md`.

**Modify:**
- `src/dsf/config/store.py` — extract `resolve_flag_key()`; refactor `InMemoryConfigStore.is_enabled` to use it.
- `src/dsf/container.py` — azure branch: per-endpoint adapter selection; `AzureRuntimeSettings` gains `openai_endpoint`/`openai_deployment`.
- `pyproject.toml` — add `[project.optional-dependencies] azure`.
- `docs/superpowers/specs/2026-06-17-dark-software-factory-template-charter-design.md` — flip SP3b row to ✅ (final task).

---

## Increment 0 — Shared flag-key resolver (prep for App Config)

### Task 1: Extract `resolve_flag_key` and refactor `InMemoryConfigStore`

**Files:**
- Modify: `src/dsf/config/store.py`
- Test: `tests/config/test_store.py`

- [ ] **Step 1: Write the failing test** — append to `tests/config/test_store.py`:

```python
from dsf.config.store import resolve_flag_key


def test_resolve_flag_key_mapping():
    assert resolve_flag_key("dry_run") == "dry_run"
    assert resolve_flag_key("critic.grounding") == "critics.grounding.enabled"
    assert resolve_flag_key("agent.SENTRY") == "agents.SENTRY.enabled"
    assert resolve_flag_key("trigger.SENTRY.paused") == "triggers.SENTRY.paused"
    assert resolve_flag_key("nonsense") is None
```

- [ ] **Step 2: Run test to verify it fails**

Run: `uv run pytest tests/config/test_store.py::test_resolve_flag_key_mapping -q`
Expected: FAIL with `ImportError: cannot import name 'resolve_flag_key'`.

- [ ] **Step 3: Implement** — in `src/dsf/config/store.py`, add the function above `class InMemoryConfigStore` and refactor `is_enabled`:

```python
def resolve_flag_key(flag: str) -> str | None:
    """Map a namespaced flag token to its underlying dotted boolean key.

    Returns ``None`` for unknown flags (``is_enabled`` then reports ``False``).
    Shared by ``InMemoryConfigStore`` and the App Configuration adapter so the
    two cannot drift.
    """
    if flag == "dry_run":
        return "dry_run"
    if flag.startswith("critic."):
        return f"critics.{flag.split('.', 1)[1]}.enabled"
    if flag.startswith("agent."):
        return f"agents.{flag.split('.', 1)[1]}.enabled"
    if flag.startswith("trigger.") and flag.endswith(".paused"):
        return f"triggers.{flag.split('.')[1]}.paused"
    return None
```

Replace the body of `InMemoryConfigStore.is_enabled` after the two override checks with:

```python
        key = resolve_flag_key(flag)
        if key is None:
            return False
        return bool(self.get_value(key, False))
```

(Delete the old inline `if flag == "dry_run" / startswith("critic.") / ...` block.)

- [ ] **Step 4: Run tests to verify they pass**

Run: `uv run pytest tests/config/test_store.py -q`
Expected: PASS (new test + all existing `InMemoryConfigStore` tests — behavior preserved).

- [ ] **Step 5: Lint + commit**

```bash
uv run ruff check src/dsf/config/store.py tests/config/test_store.py
git add src/dsf/config/store.py tests/config/test_store.py
git commit -m "refactor(config): extract resolve_flag_key shared by config stores (SP3b)

Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
```

---

## Increment 1 — App Configuration adapter

### Task 2: In-memory config gateway double

**Files:**
- Create: `tests/support/azure_doubles.py`

- [ ] **Step 1: Create the file** with the config gateway (the other doubles are added in later tasks):

```python
"""In-memory gateway doubles for offline unit tests of the Azure adapters.

The Azure adapters talk to a narrow *gateway* seam, not the raw Azure SDK.
These dict-backed gateways implement that seam so the adapters run fully
offline — the same idea as injecting ``_run`` into ``RealGitHubClient``.
"""

from __future__ import annotations

from typing import Any


class InMemoryConfigGateway:
    """Dict-backed ConfigGateway: ``(key, label) -> value`` (JSON strings)."""

    def __init__(self, seed: dict[tuple[str, str | None], str] | None = None) -> None:
        self._d: dict[tuple[str, str | None], str] = dict(seed or {})

    def get(self, key: str, label: str | None) -> str | None:
        return self._d.get((key, label))

    def set(self, key: str, value: str, label: str | None) -> None:
        self._d[(key, label)] = value

    def list(self) -> list[tuple[str, str, str | None]]:
        return [(k, v, lbl) for (k, lbl), v in self._d.items()]
```

- [ ] **Step 2: Commit** (this is a test-support primitive; committed with Task 3's tests). No standalone run.

### Task 3: `AppConfigStore`

**Files:**
- Create: `src/dsf/config/azure_store.py`
- Test: `tests/config/test_azure_store.py`

- [ ] **Step 1: Write the failing tests** — create `tests/config/test_azure_store.py`:

```python
import sys

from dsf.config.azure_store import AppConfigStore
from tests.support.azure_doubles import InMemoryConfigGateway


def _store(seed=None):
    return AppConfigStore(InMemoryConfigGateway(seed))


def test_get_value_parses_json():
    store = _store({("threshold", None): "0.7"})
    assert store.get_value("threshold") == 0.7


def test_get_value_missing_returns_default():
    assert _store().get_value("nope", default="d") == "d"


def test_is_enabled_reads_resolved_key():
    store = _store({("critics.grounding.enabled", None): "true"})
    assert store.is_enabled("critic.grounding") is True


def test_is_enabled_unknown_flag_is_false():
    assert _store().is_enabled("mystery") is False


def test_is_enabled_product_override_takes_precedence():
    store = _store(
        {
            ("agents.SENTRY.enabled", None): "false",
            ("agents.SENTRY.enabled", "acme"): "true",
        }
    )
    assert store.is_enabled("agent.SENTRY") is False
    assert store.is_enabled("agent.SENTRY", product="acme") is True


def test_set_flag_writes_resolved_key_and_label():
    gw = InMemoryConfigGateway()
    store = AppConfigStore(gw)
    store.set_flag("critic.security", True, product="acme")
    assert gw.get("critics.security.enabled", "acme") == "true"


def test_set_flag_unknown_raises():
    import pytest

    with pytest.raises(ValueError):
        _store().set_flag("bogus", True)


def test_snapshot_nests_unlabelled_and_lists_overrides():
    store = _store(
        {
            ("dry_run", None): "true",
            ("critics.security.enabled", "acme"): "false",
        }
    )
    snap = store.snapshot()
    assert snap["dry_run"] is True
    assert snap["_overrides"] == {"critics.security.enabled@acme": False}


def test_module_import_is_sdk_free():
    # Importing the adapter must not drag in the Azure SDK (lazy import only).
    assert "azure.appconfiguration" not in sys.modules
```

- [ ] **Step 2: Run to verify failure**

Run: `uv run pytest tests/config/test_azure_store.py -q`
Expected: FAIL with `ModuleNotFoundError: No module named 'dsf.config.azure_store'`.

- [ ] **Step 3: Implement** — create `src/dsf/config/azure_store.py`:

```python
"""Azure App Configuration-backed ConfigStore (real ``azure`` mode adapter).

Talks to a narrow :class:`ConfigGateway` (get/set/list of JSON-string values
keyed by ``(key, label)``). The default gateway wraps ``azure-appconfiguration``
and is built lazily, so importing this module never requires the SDK. Inject an
in-memory gateway in tests to stay offline.
"""

from __future__ import annotations

import json
from typing import Any, Protocol

from dsf.config.store import resolve_flag_key


class ConfigGateway(Protocol):
    """Narrow seam over App Configuration: string values keyed by (key, label)."""

    def get(self, key: str, label: str | None) -> str | None: ...
    def set(self, key: str, value: str, label: str | None) -> None: ...
    def list(self) -> list[tuple[str, str, str | None]]: ...


class AppConfigStore:
    """:class:`~dsf.ports.ConfigStore` backed by Azure App Configuration.

    Per-product overrides use App Configuration *labels* (label = product); a
    labelled setting takes precedence over the unlabelled default.
    """

    def __init__(self, gateway: ConfigGateway) -> None:
        self._gw = gateway

    @classmethod
    def from_endpoint(cls, endpoint: str) -> "AppConfigStore":
        """Build a store backed by the real App Configuration SDK gateway."""
        return cls(_SdkConfigGateway(endpoint))

    def is_enabled(self, flag: str, product: str | None = None) -> bool:
        key = resolve_flag_key(flag)
        if key is None:
            return False
        raw = self._gw.get(key, product) if product is not None else None
        if raw is None:
            raw = self._gw.get(key, None)
        return bool(json.loads(raw)) if raw is not None else False

    def get_value(self, key: str, default: Any = None) -> Any:
        raw = self._gw.get(key, None)
        return default if raw is None else json.loads(raw)

    def set_flag(self, flag: str, value: bool, product: str | None = None) -> None:
        key = resolve_flag_key(flag)
        if key is None:
            raise ValueError(f"unknown flag {flag!r}")
        self._gw.set(key, json.dumps(bool(value)), product)

    def snapshot(self) -> dict:
        snap: dict[str, Any] = {}
        overrides: dict[str, Any] = {}
        for key, value, label in self._gw.list():
            try:
                parsed: Any = json.loads(value)
            except (ValueError, TypeError):
                parsed = value
            if label is None:
                _assign_dotted(snap, key, parsed)
            else:
                overrides[f"{key}@{label}"] = parsed
        snap["_overrides"] = overrides
        return snap


def _assign_dotted(root: dict, dotted: str, value: Any) -> None:
    """Assign ``value`` into ``root`` along a dotted ``a.b.c`` path."""
    parts = dotted.split(".")
    node = root
    for part in parts[:-1]:
        node = node.setdefault(part, {})
    node[parts[-1]] = value


class _SdkConfigGateway:
    """Real gateway wrapping ``azure-appconfiguration`` (lazy import)."""

    def __init__(self, endpoint: str) -> None:
        self._endpoint = endpoint
        self._client: Any = None

    def _client_or_build(self) -> Any:
        if self._client is None:
            try:
                from azure.appconfiguration import AzureAppConfigurationClient
                from azure.identity import DefaultAzureCredential
            except ImportError as exc:  # pragma: no cover - requires azure extra
                raise RuntimeError(
                    "azure extra not installed; run: uv pip install -e '.[azure]'"
                ) from exc
            self._client = AzureAppConfigurationClient(
                base_url=self._endpoint, credential=DefaultAzureCredential()
            )
        return self._client

    def get(self, key: str, label: str | None) -> str | None:  # pragma: no cover
        client = self._client_or_build()
        try:
            setting = client.get_configuration_setting(key=key, label=label)
        except Exception as exc:
            if type(exc).__name__ == "ResourceNotFoundError":
                return None
            raise
        return setting.value

    def set(self, key: str, value: str, label: str | None) -> None:  # pragma: no cover
        from azure.appconfiguration import ConfigurationSetting

        client = self._client_or_build()
        client.set_configuration_setting(
            ConfigurationSetting(key=key, value=value, label=label)
        )

    def list(self) -> list[tuple[str, str, str | None]]:  # pragma: no cover
        client = self._client_or_build()
        return [
            (s.key, s.value, s.label)
            for s in client.list_configuration_settings()
        ]
```

- [ ] **Step 4: Run to verify pass**

Run: `uv run pytest tests/config/test_azure_store.py -q`
Expected: PASS (9 tests).

- [ ] **Step 5: Lint + commit**

```bash
uv run ruff check src/dsf/config/azure_store.py tests/config/test_azure_store.py tests/support/azure_doubles.py
git add src/dsf/config/azure_store.py tests/config/test_azure_store.py tests/support/azure_doubles.py
git commit -m "feat(config): AppConfigStore over Azure App Configuration behind ConfigStore (SP3b)

Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
```

---

## Increment 2 — Cosmos memory adapter

### Task 4: In-memory Cosmos gateway double

**Files:**
- Modify: `tests/support/azure_doubles.py`

- [ ] **Step 1: Append the Cosmos gateway**:

```python
class InMemoryCosmosGateway:
    """Dict-backed CosmosGateway: ``container -> list[item dict]``.

    Implements ``upsert`` (replace-by-``id``) and a single-field equality
    ``query`` — the only query shape the adapter issues.
    """

    def __init__(self) -> None:
        self.containers: dict[str, list[dict]] = {}

    async def upsert(self, container: str, item: dict) -> None:
        items = self.containers.setdefault(container, [])
        for idx, existing in enumerate(items):
            if existing.get("id") == item.get("id"):
                items[idx] = dict(item)
                return
        items.append(dict(item))

    async def query(self, container: str, field: str, value: Any) -> list[dict]:
        return [
            dict(i) for i in self.containers.get(container, []) if i.get(field) == value
        ]
```

- [ ] **Step 2: Commit** with Task 5's tests (test-support primitive). No standalone run.

### Task 5: `CosmosMemoryStore`

**Files:**
- Create: `src/dsf/memory/azure_store.py`
- Test: `tests/memory/test_azure_store.py`

- [ ] **Step 1: Write the failing tests** — create `tests/memory/test_azure_store.py`:

```python
import sys

from dsf.memory.azure_store import CosmosMemoryStore
from tests.support.azure_doubles import InMemoryCosmosGateway


def _store():
    return CosmosMemoryStore(InMemoryCosmosGateway())


async def test_put_and_get_working():
    store = _store()
    await store.put_working("k", {"v": 1})
    assert await store.get_working("k") == {"v": 1}


async def test_get_working_missing_returns_none():
    assert await _store().get_working("absent") is None


async def test_put_working_sets_ttl_field():
    gw = InMemoryCosmosGateway()
    store = CosmosMemoryStore(gw)
    await store.put_working("k", 1, ttl=30)
    assert gw.containers["working"][0]["ttl"] == 30


async def test_query_similar_ranks_by_overlap_and_limits_k():
    store = _store()
    await store.put_record({"kind": "bug", "text": "login fails on safari"})
    await store.put_record({"kind": "bug", "text": "unrelated payment timeout"})
    await store.put_record({"kind": "other", "text": "login fails on safari"})
    out = await store.query_similar("login fails safari", "bug", k=1)
    assert len(out) == 1
    assert "login" in out[0]["text"]
    assert out[0]["similarity"] > 0


async def test_get_lessons_filters_product_newest_first_limit_k():
    store = _store()
    await store.put_lesson({"product": "acme", "text": "first"})
    await store.put_lesson({"product": "acme", "text": "second"})
    await store.put_lesson({"product": "other", "text": "nope"})
    out = await store.get_lessons("acme", k=1)
    assert [le["text"] for le in out] == ["second"]


def test_module_import_is_sdk_free():
    assert "azure.cosmos" not in sys.modules
```

- [ ] **Step 2: Run to verify failure**

Run: `uv run pytest tests/memory/test_azure_store.py -q`
Expected: FAIL with `ModuleNotFoundError: No module named 'dsf.memory.azure_store'`.

- [ ] **Step 3: Implement** — create `src/dsf/memory/azure_store.py`:

```python
"""Azure Cosmos DB-backed MemoryStore (real ``azure`` mode adapter).

Talks to a narrow :class:`CosmosGateway` (``upsert`` + single-field equality
``query``). The default gateway wraps ``azure-cosmos`` (aio) and is built
lazily. Ranking for ``query_similar`` reuses the same token-overlap scorer as
``InMemoryMemoryStore`` (native vector search is deferred — see ADR 0006).
"""

from __future__ import annotations

from typing import Any, Protocol

from dsf.memory.store import _overlap

_WORKING = "working"
_RECORDS = "records"
_LESSONS = "lessons"


class CosmosGateway(Protocol):
    """Narrow async seam over Cosmos data-plane operations."""

    async def upsert(self, container: str, item: dict) -> None: ...
    async def query(self, container: str, field: str, value: Any) -> list[dict]: ...


class CosmosMemoryStore:
    """:class:`~dsf.ports.MemoryStore` backed by Cosmos DB (one DB per product)."""

    def __init__(self, gateway: CosmosGateway) -> None:
        self._gw = gateway
        self._seq = 0

    @classmethod
    def from_endpoint(cls, endpoint: str, *, database: str) -> "CosmosMemoryStore":
        """Build a store backed by the real Cosmos SDK gateway."""
        return cls(_SdkCosmosGateway(endpoint, database))

    def _next_seq(self) -> int:
        self._seq += 1
        return self._seq

    async def put_working(self, key: str, value: Any, ttl: float | None = None) -> None:
        item: dict[str, Any] = {"id": key, "key": key, "value": value}
        if ttl is not None:
            item["ttl"] = int(ttl)
        await self._gw.upsert(_WORKING, item)

    async def get_working(self, key: str) -> Any | None:
        rows = await self._gw.query(_WORKING, "key", key)
        return rows[0]["value"] if rows else None

    async def put_record(self, record: dict, ttl: float | None = None) -> None:
        seq = self._next_seq()
        item = dict(record)
        item.setdefault("id", f"rec-{seq}")
        item["_seq"] = seq
        if ttl is not None:
            item["ttl"] = int(ttl)
        await self._gw.upsert(_RECORDS, item)

    async def query_similar(self, text: str, kind: str, k: int = 5) -> list[dict]:
        rows = await self._gw.query(_RECORDS, "kind", kind)
        scored = sorted(
            ((_overlap(text, str(r.get("text", ""))), r) for r in rows),
            key=lambda pair: pair[0],
            reverse=True,
        )
        return [
            {rk: rv for rk, rv in r.items() if not rk.startswith("_") and rk != "ttl"}
            | {"similarity": sim}
            for sim, r in scored[:k]
        ]

    async def put_lesson(self, lesson: dict) -> None:
        seq = self._next_seq()
        item = dict(lesson)
        item.setdefault("id", f"lesson-{seq}")
        item["_seq"] = seq
        await self._gw.upsert(_LESSONS, item)

    async def get_lessons(self, product: str, k: int = 5) -> list[dict]:
        rows = await self._gw.query(_LESSONS, "product", product)
        rows.sort(key=lambda r: r.get("_seq", 0))
        return [
            {rk: rv for rk, rv in r.items() if not rk.startswith("_")}
            for r in rows[-k:][::-1]
        ]


class _SdkCosmosGateway:
    """Real gateway wrapping ``azure-cosmos`` aio (lazy import).

    ``field`` is always an internal constant (``key``/``kind``/``product``), so
    the f-string query is not user-controlled.
    """

    def __init__(self, endpoint: str, database: str) -> None:
        self._endpoint = endpoint
        self._database = database
        self._client: Any = None

    def _container(self, name: str) -> Any:  # pragma: no cover - requires azure extra
        if self._client is None:
            try:
                from azure.cosmos.aio import CosmosClient
                from azure.identity.aio import DefaultAzureCredential
            except ImportError as exc:
                raise RuntimeError(
                    "azure extra not installed; run: uv pip install -e '.[azure]'"
                ) from exc
            self._client = CosmosClient(
                self._endpoint, credential=DefaultAzureCredential()
            )
        return self._client.get_database_client(self._database).get_container_client(name)

    async def upsert(self, container: str, item: dict) -> None:  # pragma: no cover
        await self._container(container).upsert_item(item)

    async def query(self, container: str, field: str, value: Any) -> list[dict]:  # pragma: no cover
        cont = self._container(container)
        query = f"SELECT * FROM c WHERE c.{field} = @value"
        params = [{"name": "@value", "value": value}]
        return [item async for item in cont.query_items(query=query, parameters=params)]
```

- [ ] **Step 4: Run to verify pass**

Run: `uv run pytest tests/memory/test_azure_store.py -q`
Expected: PASS (6 tests).

- [ ] **Step 5: Lint + commit**

```bash
uv run ruff check src/dsf/memory/azure_store.py tests/memory/test_azure_store.py tests/support/azure_doubles.py
git add src/dsf/memory/azure_store.py tests/memory/test_azure_store.py tests/support/azure_doubles.py
git commit -m "feat(memory): CosmosMemoryStore over Cosmos DB behind MemoryStore (SP3b)

Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
```

---

## Increment 3 — Azure OpenAI model adapter

### Task 6: Recording chat gateway double

**Files:**
- Modify: `tests/support/azure_doubles.py`

- [ ] **Step 1: Append the chat gateway**:

```python
class RecordingChatGateway:
    """ChatGateway double: records calls, returns a canned ``response`` string."""

    def __init__(self, response: str = "") -> None:
        self.response = response
        self.calls: list[dict] = []

    async def complete(self, system: str, prompt: str, json_schema: dict | None) -> str:
        self.calls.append(
            {"system": system, "prompt": prompt, "json_schema": json_schema}
        )
        return self.response
```

- [ ] **Step 2: Commit** with Task 7's tests. No standalone run.

### Task 7: `AzureOpenAIModelClient`

**Files:**
- Create: `src/dsf/model/azure_client.py`
- Test: `tests/model/test_azure_client.py`

- [ ] **Step 1: Write the failing tests** — create `tests/model/test_azure_client.py`:

```python
import sys

from pydantic import BaseModel

from dsf.model.azure_client import AzureOpenAIModelClient
from tests.support.azure_doubles import RecordingChatGateway


class _Verdict(BaseModel):
    accept: bool
    reason: str


async def test_complete_returns_prose_without_schema():
    client = AzureOpenAIModelClient(RecordingChatGateway(response="hello there"))
    assert await client.complete("sys", "say hi") == "hello there"


async def test_complete_parses_into_schema():
    gw = RecordingChatGateway(response='{"accept": true, "reason": "grounded"}')
    client = AzureOpenAIModelClient(gw)
    out = await client.complete("sys", "judge", schema=_Verdict)
    assert isinstance(out, _Verdict)
    assert out.accept is True
    assert out.reason == "grounded"


async def test_complete_passes_json_schema_when_schema_given():
    gw = RecordingChatGateway(response='{"accept": false, "reason": "x"}')
    await AzureOpenAIModelClient(gw).complete("sys", "judge", schema=_Verdict)
    sent = gw.calls[0]["json_schema"]
    assert sent is not None
    assert sent["name"] == "_Verdict"
    assert "accept" in sent["schema"]["properties"]


async def test_complete_no_schema_passes_none():
    gw = RecordingChatGateway(response="prose")
    await AzureOpenAIModelClient(gw).complete("sys", "p")
    assert gw.calls[0]["json_schema"] is None


def test_module_import_is_sdk_free():
    assert "openai" not in sys.modules
```

- [ ] **Step 2: Run to verify failure**

Run: `uv run pytest tests/model/test_azure_client.py -q`
Expected: FAIL with `ModuleNotFoundError: No module named 'dsf.model.azure_client'`.

- [ ] **Step 3: Implement** — create `src/dsf/model/azure_client.py`:

```python
"""Azure OpenAI-backed ModelClient (real ``azure`` mode adapter).

Talks to a narrow :class:`ChatGateway` (one ``complete`` call returning the raw
content string). The default gateway wraps the ``openai`` Async Azure client and
is built lazily. When a pydantic ``schema`` is supplied, the adapter requests a
JSON-schema structured response and validates the content into the model.
"""

from __future__ import annotations

from typing import Any, Protocol

from pydantic import BaseModel


class ChatGateway(Protocol):
    """Narrow async seam over a chat-completion call."""

    async def complete(
        self, system: str, prompt: str, json_schema: dict | None
    ) -> str: ...


class AzureOpenAIModelClient:
    """:class:`~dsf.ports.ModelClient` backed by Azure OpenAI."""

    def __init__(self, gateway: ChatGateway) -> None:
        self._gw = gateway

    @classmethod
    def from_endpoint(
        cls, endpoint: str, *, deployment: str, api_version: str = "2024-10-21"
    ) -> "AzureOpenAIModelClient":
        """Build a client backed by the real Azure OpenAI SDK gateway."""
        return cls(_SdkChatGateway(endpoint, deployment, api_version))

    async def complete(
        self,
        system: str,
        prompt: str,
        schema: type[BaseModel] | None = None,
    ) -> BaseModel | str:
        json_schema: dict | None = None
        if schema is not None:
            json_schema = {"name": schema.__name__, "schema": schema.model_json_schema()}
        content = await self._gw.complete(system, prompt, json_schema)
        if schema is not None:
            return schema.model_validate_json(content)
        return content


class _SdkChatGateway:
    """Real gateway wrapping ``openai`` Async Azure client (lazy import)."""

    def __init__(self, endpoint: str, deployment: str, api_version: str) -> None:
        self._endpoint = endpoint
        self._deployment = deployment
        self._api_version = api_version
        self._client: Any = None

    def _client_or_build(self) -> Any:  # pragma: no cover - requires azure extra
        if self._client is None:
            try:
                from azure.identity import (
                    DefaultAzureCredential,
                    get_bearer_token_provider,
                )
                from openai import AsyncAzureOpenAI
            except ImportError as exc:
                raise RuntimeError(
                    "azure extra not installed; run: uv pip install -e '.[azure]'"
                ) from exc
            token_provider = get_bearer_token_provider(
                DefaultAzureCredential(),
                "https://cognitiveservices.azure.com/.default",
            )
            self._client = AsyncAzureOpenAI(
                azure_endpoint=self._endpoint,
                azure_ad_token_provider=token_provider,
                api_version=self._api_version,
            )
        return self._client

    async def complete(
        self, system: str, prompt: str, json_schema: dict | None
    ) -> str:  # pragma: no cover
        client = self._client_or_build()
        kwargs: dict[str, Any] = {
            "model": self._deployment,
            "messages": [
                {"role": "system", "content": system},
                {"role": "user", "content": prompt},
            ],
        }
        if json_schema is not None:
            kwargs["response_format"] = {
                "type": "json_schema",
                "json_schema": json_schema,
            }
        resp = await client.chat.completions.create(**kwargs)
        return resp.choices[0].message.content or ""
```

- [ ] **Step 4: Run to verify pass**

Run: `uv run pytest tests/model/test_azure_client.py -q`
Expected: PASS (5 tests).

- [ ] **Step 5: Lint + commit**

```bash
uv run ruff check src/dsf/model/azure_client.py tests/model/test_azure_client.py tests/support/azure_doubles.py
git add src/dsf/model/azure_client.py tests/model/test_azure_client.py tests/support/azure_doubles.py
git commit -m "feat(model): AzureOpenAIModelClient behind ModelClient (SP3b)

Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
```

---

## Increment 4 — Wire into `build_services('azure')` + deps + ADR

### Task 8: Container wiring with per-endpoint fallback + `azure` extra

**Files:**
- Modify: `src/dsf/container.py`
- Modify: `pyproject.toml`
- Test: `tests/test_container.py`

- [ ] **Step 1: Write the failing tests** — append to `tests/test_container.py`:

```python
from dsf.config.azure_store import AppConfigStore
from dsf.config.store import InMemoryConfigStore
from dsf.memory.azure_store import CosmosMemoryStore
from dsf.memory.store import InMemoryMemoryStore
from dsf.model.azure_client import AzureOpenAIModelClient
from dsf.model.client import DeterministicModelClient


def test_azure_falls_back_to_inmemory_without_endpoints():
    svc = build_services("azure", env={"DSF_PRODUCT": "acme"})
    assert isinstance(svc.config, InMemoryConfigStore)
    assert isinstance(svc.memory, InMemoryMemoryStore)
    assert isinstance(svc.model, DeterministicModelClient)


def test_azure_uses_appconfig_when_endpoint_set():
    svc = build_services(
        "azure",
        env={"DSF_PRODUCT": "acme", "AZURE_APPCONFIG_ENDPOINT": "https://ac.example"},
    )
    assert isinstance(svc.config, AppConfigStore)


def test_azure_uses_cosmos_when_endpoint_set():
    svc = build_services(
        "azure",
        env={"DSF_PRODUCT": "acme", "AZURE_COSMOS_ENDPOINT": "https://cosmos.example"},
    )
    assert isinstance(svc.memory, CosmosMemoryStore)


def test_azure_uses_openai_when_endpoint_and_deployment_set():
    svc = build_services(
        "azure",
        env={
            "DSF_PRODUCT": "acme",
            "AZURE_OPENAI_ENDPOINT": "https://oai.example",
            "AZURE_OPENAI_DEPLOYMENT": "gpt-4o",
        },
    )
    assert isinstance(svc.model, AzureOpenAIModelClient)


def test_azure_openai_needs_both_endpoint_and_deployment():
    svc = build_services(
        "azure",
        env={"DSF_PRODUCT": "acme", "AZURE_OPENAI_ENDPOINT": "https://oai.example"},
    )
    assert isinstance(svc.model, DeterministicModelClient)
```

(If `build_services` is not already imported at the top of `tests/test_container.py`, add `from dsf.container import build_services`.)

- [ ] **Step 2: Run to verify failure**

Run: `uv run pytest tests/test_container.py -q`
Expected: FAIL (`AttributeError`/`AssertionError`: azure branch still wires in-memory unconditionally; `openai_endpoint` not yet on settings).

- [ ] **Step 3a: Add settings fields** — in `src/dsf/container.py`, add to `AzureRuntimeSettings`:

```python
    openai_endpoint: str = ""
    openai_deployment: str = ""
```

And in `AzureRuntimeSettings.from_env`, inside the `return cls(...)`, add:

```python
            openai_endpoint=(env.get("AZURE_OPENAI_ENDPOINT") or "").strip(),
            openai_deployment=(env.get("AZURE_OPENAI_DEPLOYMENT") or "").strip(),
```

- [ ] **Step 3b: Replace the azure branch body** — in `build_services`, replace the `if mode == "azure":` block with:

```python
    if mode == "azure":
        from dsf.github_client import RealGitHubClient
        from dsf.observability.tracing import build_tracer

        settings = AzureRuntimeSettings.from_env(env if env is not None else os.environ)

        config: ConfigStore
        if settings.appconfig_endpoint:
            from dsf.config.azure_store import AppConfigStore

            config = AppConfigStore.from_endpoint(settings.appconfig_endpoint)
        else:
            config = InMemoryConfigStore.from_defaults()

        memory: MemoryStore
        if settings.cosmos_endpoint:
            from dsf.memory.azure_store import CosmosMemoryStore

            memory = CosmosMemoryStore.from_endpoint(
                settings.cosmos_endpoint, database=settings.product
            )
        else:
            memory = InMemoryMemoryStore()

        model: ModelClient
        if settings.openai_endpoint and settings.openai_deployment:
            from dsf.model.azure_client import AzureOpenAIModelClient

            model = AzureOpenAIModelClient.from_endpoint(
                settings.openai_endpoint, deployment=settings.openai_deployment
            )
        else:
            model = DeterministicModelClient()

        return Services(
            mode=mode,
            model=model,
            memory=memory,
            config=config,
            github=RealGitHubClient(),
            tracer=build_tracer("azure"),
            product=settings.product,
            azure=settings,
        )
```

- [ ] **Step 3c: Update the `azure` docstring** — in `build_services`, replace the sentence "keeps model/memory/config on in-memory implementations behind the deferred-adapter seam (SP3b)." with: "wires the real Azure data adapters (App Configuration / Cosmos / Azure OpenAI) for each configured endpoint, falling back to the in-memory sibling when an endpoint is unset (SP3b)."

- [ ] **Step 3d: Add the `azure` extra** — in `pyproject.toml`, under `[project.optional-dependencies]`, add:

```toml
azure = [
    "azure-appconfiguration>=1.5",
    "azure-cosmos>=4.5",
    "azure-identity>=1.15",
    "openai>=1.30",
]
```

- [ ] **Step 4: Run to verify pass**

Run: `uv run pytest tests/test_container.py -q`
Expected: PASS (new azure tests + all existing container tests).

- [ ] **Step 5: Full suite + eval gate + lint**

Run: `uv run pytest -q` → Expected: PASS (all prior + new).
Run: `uv run python -m dsf.evals.runner --gate` → Expected: `GATE PASSED`.
Run: `uv run ruff check .` → Expected: `All checks passed!`.

- [ ] **Step 6: Commit**

```bash
git add src/dsf/container.py pyproject.toml tests/test_container.py
git commit -m "feat(container): wire Azure adapters per endpoint in azure mode; add azure extra (SP3b)

Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
```

### Task 9: ADR 0006 + charter status

**Files:**
- Create: `docs/adr/0006-azure-data-adapters.md`
- Modify: `docs/superpowers/specs/2026-06-17-dark-software-factory-template-charter-design.md`

- [ ] **Step 1: Write ADR 0006** — create `docs/adr/0006-azure-data-adapters.md`:

```markdown
# ADR 0006: Azure data adapters — injected gateway seams, optional extra, graceful degradation

- Status: Accepted
- Date: 2026-06-18
- Fulfils: ADR 0001 (ports) and ADR 0005 (honest local implementations); supersedes nothing.

## Context

SP3 wired a real GitHub client and tracer in `azure` mode but kept config,
memory, and model on the in-memory implementations behind a deferred-adapter
seam. SP3b lands the real Azure data adapters.

## Decision

- Implement `AppConfigStore` (Azure App Configuration), `CosmosMemoryStore`
  (Cosmos DB), and `AzureOpenAIModelClient` (Azure OpenAI) behind the existing
  `ConfigStore` / `MemoryStore` / `ModelClient` ports — no caller changes.
- Each adapter consumes a **narrow gateway** `Protocol` (1-3 methods) instead of
  the raw SDK. The default gateway wraps the SDK and is built lazily from an
  endpoint + `DefaultAzureCredential`; tests inject a dict-backed in-memory
  gateway. This keeps adapters free of SDK object/exception quirks and keeps the
  whole suite offline — the same seam idea as `RealGitHubClient(_run=...)`.
- Azure SDKs are an **optional `azure` extra**, lazy-imported inside the gateway
  builders. Importing an adapter module never requires the SDK; only building a
  real gateway does (raising a clear error if the extra is absent).
- `build_services('azure')` selects each real adapter **only when its endpoint is
  configured**, falling back to the in-memory sibling otherwise — so `azure` mode
  runs mid-rollout and the offline test/eval posture is unchanged.

## Consequences

- Real Azure data paths exist behind the ports, unit-tested offline; live-Azure
  integration testing is out of scope (billable; covered structurally by SP2).
- Cosmos `query_similar` keeps token-overlap ranking; native vector search is
  deferred.
- The `[deterministic]` synthesizer echo only comes from `DeterministicModelClient`;
  the real model returns real content, so the fallback simply never triggers.
```

- [ ] **Step 2: Flip the charter SP3b row** — in `docs/superpowers/specs/2026-06-17-dark-software-factory-template-charter-design.md`, change the SP3b table row marker from `| SP3b |` to `| SP3b ✅ |` and append " *(done — ADR 0006)*" to its Outcome cell.

- [ ] **Step 3: Commit**

```bash
git add docs/adr/0006-azure-data-adapters.md docs/superpowers/specs/2026-06-17-dark-software-factory-template-charter-design.md
git commit -m "docs(adr): ADR 0006 Azure data adapters; mark SP3b done in charter (SP3b)

Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
```

---

## Self-Review notes (resolved)

- **Spec coverage:** App Config (Task 3) · Cosmos (Task 5) · Azure OpenAI (Task 7) · injected-gateway pattern + lazy import + optional extra (all adapters) · graceful per-endpoint wiring (Task 8) · ADR 0006 (Task 9) · resolve_flag_key shared mapping (Task 1). All spec §3-§8 covered.
- **Offline posture:** every adapter test injects an in-memory gateway; import-safety tests assert no top-level SDK import; eval gate re-run in Task 8.
- **Type consistency:** gateway method names (`get`/`set`/`list`, `upsert`/`query`, `complete`) and `from_endpoint(...)` factories are used identically across plan and tests. `resolve_flag_key` returns `str | None`; `is_enabled` treats `None` as `False` in both stores.
- **Done-when:** `uv run ruff check .` clean · `uv run pytest -q` green · `uv run python -m dsf.evals.runner --gate` PASSED.
