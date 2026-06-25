# WebIQ SDK Source Agent Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace the WebIQ source agent's flaky Azure-Foundry "Grounding with Bing Search" backend with the real Microsoft **WebIQ SDK** (`pip install webiq`, API-key auth), and rip every trace of Bing/Foundry-project provisioning out of the CLI + infra.

**Architecture:** The `webiq` source agent gets a new provider, `webiq` (now the default), implemented in a self-contained module that drives `webiq.WebIQAsyncClient.web.search(...)` and maps each result to the agent's `{finding, url, confidence}` dict. The API key is resolved app-side: `WEBIQ_API_KEY` env override, else read from the product Key Vault (secret `webiq-api-key`) — exactly like the GitHub App private key. Provisioning seeds that key from the owner vault into the product vault (mirroring `_seed_app_key`), and the Bing account + Foundry project + their params/outputs/connection-step are deleted. Tavily stays selectable via `WEBIQ_PROVIDER=tavily`.

**Tech Stack:** Python 3.12, `uv` workspace, `httpx` (test transport), `webiq` SDK, Azure Bicep, `az` CLI, pytest (`asyncio_mode=auto`), ruff, import-linter.

---

## Divergences from the spec (`docs/superpowers/specs/2026-06-25-webiq-sdk-source-agent-design.md`)

Two design points were refined after reading the exact code; this plan supersedes the spec on both, and Task E3 updates the spec text to match:

1. **The Foundry *project* (`aiProject`) is removed too, not kept.** Investigation (`grep AZURE_AI_PROJECT_ENDPOINT/aiProjectEndpoint`) proved the project + its endpoint are consumed *only* by the deleted `foundry.py`, `runtime_render._ENDPOINT_MAP`, and the Bing bicep. Nothing else needs it, so leaving it would be vestigial. The Foundry **account** (`foundry`, Azure OpenAI for chat/embeddings) stays.
2. **Seed writes set `--content-type text/plain` + `--expires`.** The owner vault enforces an MG-scoped deny policy (expiry + content-type required); the per-product vaults share that subscription/MG, so the seed writes must satisfy it. Expiry window = **30 days** (verified-acceptable; re-seeded on every `dsf new` / rotation). This is applied to both `_seed_webiq_key` (new) and `_seed_app_key` (existing — otherwise `dsf new` breaks at that step).

---

## File Structure

**feature-council (the agent):**
- `feature-council/src/dsf/agents/webiq/webiq_sdk.py` — **CREATE**. The WebIQ-SDK search builder + KV key resolution. One responsibility: turn env/key into an async `search(query) -> list[dict]`.
- `feature-council/src/dsf/agents/webiq/client.py` — **MODIFY**. Provider switch: default `webiq`, dispatch to `webiq_sdk`/tavily, drop foundry.
- `feature-council/src/dsf/agents/webiq/foundry.py` — **DELETE**.
- `feature-council/src/dsf/agents/webiq/main.py` — **MODIFY** (docstrings only).
- `feature-council/src/dsf/agents/webiq/README.md` — **MODIFY** (provider table).
- `feature-council/tests/agents/webiq/test_webiq_sdk.py` — **CREATE** (WebIQ provider tests).
- `feature-council/tests/agents/webiq/test_webiq_foundry.py` — **DELETE**.
- `feature-council/pyproject.toml` — **MODIFY** (`uv add webiq`).

**cli (provisioning):**
- `cli/src/dsf/instance/provisioner.py` — **MODIFY**. Remove all Bing code; add `seed_webiq_key` step + `_seed_webiq_key`; add seed expiry/content-type (both seeds).
- `cli/src/dsf/instance/spec.py` — **MODIFY**. Drop `enable_bing_grounding`.
- `cli/src/dsf/instance/runtime_render.py` — **MODIFY**. Drop the two Bing `_ENDPOINT_MAP` rows; add static `WEBIQ_PROVIDER`/`WEBIQ_API_KEY_SECRET`.
- `cli/src/dsf/cli/factory.py` — **MODIFY**. Drop `--enable-bing-grounding`.
- `cli/tests/instance/test_provisioner.py` — **MODIFY**.
- `cli/tests/cli/test_factory.py` — **MODIFY**.
- `cli/tests/instance/test_deploy_progress.py` — **MODIFY** (cosmetic sample name).

**infra + docs:**
- `infra/main.bicep` — **MODIFY**. Delete Bing account, Foundry project, role assignment, params, vars, outputs, container env; set `WEBIQ_PROVIDER='webiq'`, add `WEBIQ_API_KEY_SECRET='webiq-api-key'`.
- `docs/adr/0020-webiq-via-webiq-sdk.md` — **CREATE**.
- `docs/site/get-started/provision-a-factory.md` — **MODIFY** (drop Bing recovery note).
- `docs/superpowers/specs/2026-06-25-webiq-sdk-source-agent-design.md` — **MODIFY** (reconcile the two divergences).

---

## Gates (run from repo root; all must pass)

```bash
uv run pytest -q
uv run ruff check .
uv run lint-imports          # expect: 4 kept, 0 broken
az bicep build --file infra/main.bicep --stdout > /dev/null && echo BICEP_OK
```

`az` is slow — allow a long wait on the bicep build.

---

## Phase A — WebIQ SDK agent (feature-council)

### Task A1: Add the `webiq` dependency

**Files:**
- Modify: `feature-council/pyproject.toml` (dependencies) + `uv.lock`

- [ ] **Step 1: Add the dependency via uv**

Run: `uv add --package dsf-feature-council webiq`
Expected: `feature-council/pyproject.toml` gains `"webiq"` in `[project.dependencies]`; `uv.lock` updates; install succeeds.

- [ ] **Step 2: Verify the import resolves**

Run: `uv run python -c "from webiq import WebIQAsyncClient; from webiq.types import ContentFormat; print('webiq ok')"`
Expected: prints `webiq ok`.

- [ ] **Step 3: Commit**

```bash
git add feature-council/pyproject.toml uv.lock
git commit -m "build: add webiq SDK dependency to feature-council"
```

---

### Task A2: Create `webiq_sdk.py` (the SDK-backed search builder)

**Files:**
- Create: `feature-council/src/dsf/agents/webiq/webiq_sdk.py`
- Test: `feature-council/tests/agents/webiq/test_webiq_sdk.py`

- [ ] **Step 1: Write the failing tests**

Create `feature-council/tests/agents/webiq/test_webiq_sdk.py`:

```python
"""WebIQ-SDK provider tests (no network).

Drives the real ``webiq`` SDK through an injected ``httpx.AsyncClient`` backed
by ``httpx.MockTransport``, so the production code path is exercised without
touching the network (ADR 0014: real code, deterministic test seams).
"""

from __future__ import annotations

import httpx
import pytest

from dsf.agents.webiq.client import build_webiq_client_from_env
from dsf.agents.webiq.webiq_sdk import _build_webiq_search

_BASE_URL = "https://api.microsoft.ai/v3"
_CANNED = {
    "webResults": [
        {"title": "T", "url": "https://ex.com/p", "content": "finding text"},
        {"title": "", "url": "", "content": ""},  # dropped: no finding, no url
    ],
    "traceId": "t1",
}


def _mock_client(captured: dict) -> httpx.AsyncClient:
    def handler(request: httpx.Request) -> httpx.Response:
        captured["host"] = request.url.host
        captured["path"] = request.url.path
        captured["method"] = request.method
        return httpx.Response(200, json=_CANNED)

    return httpx.AsyncClient(base_url=_BASE_URL, transport=httpx.MockTransport(handler))


async def test_search_hits_webiq_and_maps_results(monkeypatch):
    monkeypatch.setenv("WEBIQ_API_KEY", "wq-key")
    captured: dict = {}
    search = _build_webiq_search(client=_mock_client(captured))

    out = await search("microbi competitor release")

    assert captured["method"] == "POST"
    assert captured["host"] == "api.microsoft.ai"
    assert captured["path"].endswith("/search/web")
    assert len(out) == 1  # the empty row is dropped
    assert out[0] == {
        "finding": "finding text",
        "url": "https://ex.com/p",
        "confidence": 0.6,
    }


async def test_key_read_from_vault_when_env_absent(monkeypatch):
    monkeypatch.delenv("WEBIQ_API_KEY", raising=False)
    monkeypatch.setenv("AZURE_KEYVAULT_URI", "https://kv-demo.vault.azure.net/")
    monkeypatch.setenv("WEBIQ_API_KEY_SECRET", "webiq-api-key")
    seen: dict = {}

    def fake_reader(uri: str, name: str) -> str:
        seen["uri"], seen["name"] = uri, name
        return "kv-key"

    search = _build_webiq_search(client=_mock_client({}), key_reader=fake_reader)
    await search("q")

    assert seen == {"uri": "https://kv-demo.vault.azure.net/", "name": "webiq-api-key"}


async def test_env_key_short_circuits_vault(monkeypatch):
    monkeypatch.setenv("WEBIQ_API_KEY", "env-key")

    def boom(uri: str, name: str) -> str:  # must not be called
        raise AssertionError("key_reader should not run when WEBIQ_API_KEY is set")

    search = _build_webiq_search(client=_mock_client({}), key_reader=boom)
    assert await search("q") == [
        {"finding": "finding text", "url": "https://ex.com/p", "confidence": 0.6}
    ]


def test_vault_read_requires_keyvault_uri(monkeypatch):
    monkeypatch.delenv("WEBIQ_API_KEY", raising=False)
    monkeypatch.delenv("AZURE_KEYVAULT_URI", raising=False)
    with pytest.raises(RuntimeError, match="AZURE_KEYVAULT_URI"):
        _build_webiq_search(client=_mock_client({}), key_reader=lambda u, n: "x")


def test_foundry_provider_now_unsupported(monkeypatch):
    monkeypatch.setenv("WEBIQ_PROVIDER", "foundry")
    monkeypatch.setenv("WEBIQ_API_KEY", "wq-key")
    with pytest.raises(NotImplementedError):
        build_webiq_client_from_env()
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `uv run pytest feature-council/tests/agents/webiq/test_webiq_sdk.py -q`
Expected: FAIL — `ModuleNotFoundError: dsf.agents.webiq.webiq_sdk` (and `_build_webiq_search` undefined).

- [ ] **Step 3: Create the module**

Create `feature-council/src/dsf/agents/webiq/webiq_sdk.py`:

```python
"""WebIQ-SDK web-search backend for :class:`WebIqMcpBackend`.

Builds an async ``search(query) -> list[dict]`` callable on top of the real
Microsoft **WebIQ** SDK (``pip install webiq``). The API key is resolved
app-side — ``WEBIQ_API_KEY`` env override, else read from the product Key Vault
(secret named by ``WEBIQ_API_KEY_SECRET``, default ``webiq-api-key``) at
``AZURE_KEYVAULT_URI`` — mirroring how the GitHub App private key is read. The
key is seeded into that vault at provision time (see
``InstanceProvisioner._seed_webiq_key``), so a deploy-time ACA secret reference
cannot work; the read happens here, at runtime.

Tests inject an ``httpx.AsyncClient`` (used by the SDK as-is) and a
``key_reader`` so the real SDK path runs with no network and no Azure.
"""

from __future__ import annotations

import os
from typing import TYPE_CHECKING

from dsf.agents.mode import env_required

if TYPE_CHECKING:
    from collections.abc import Awaitable, Callable

    import httpx

#: Per-result confidence. The WebIQ API exposes no per-result score, so we use
#: the same constant the prior web-research path used.
_DEFAULT_CONFIDENCE = 0.6
#: Default Key Vault secret name holding the WebIQ API key.
_DEFAULT_KEY_SECRET = "webiq-api-key"


def _read_kv_secret(vault_uri: str, secret_name: str) -> str:  # pragma: no cover - real Azure I/O
    """Read a secret value from Azure Key Vault via the ambient managed identity.

    Kept local (not imported from ``dsf.container``) so the agent stays
    self-contained and importing it triggers no heavy Azure imports; the Azure
    SDKs are imported lazily here and only on the real runtime path.
    """
    from azure.identity import DefaultAzureCredential
    from azure.keyvault.secrets import SecretClient

    client = SecretClient(vault_url=vault_uri, credential=DefaultAzureCredential())
    return client.get_secret(secret_name).value or ""


def _resolve_api_key(key_reader: Callable[[str, str], str] | None) -> str:
    """Resolve the WebIQ API key: env override, else Key Vault read."""
    direct = os.environ.get("WEBIQ_API_KEY")
    if direct:
        return direct
    vault_uri = env_required(
        "AZURE_KEYVAULT_URI", hint="product Key Vault URI holding the WebIQ API key"
    )
    secret_name = os.environ.get("WEBIQ_API_KEY_SECRET") or _DEFAULT_KEY_SECRET
    reader = key_reader or _read_kv_secret
    return reader(vault_uri, secret_name)


def _max_results() -> int:
    """Result cap from ``WEBIQ_MAX_RESULTS`` (default 5)."""
    try:
        return max(1, int(os.environ.get("WEBIQ_MAX_RESULTS") or "5"))
    except ValueError:
        return 5


def _build_webiq_search(
    *,
    client: httpx.AsyncClient | None = None,
    key_reader: Callable[[str, str], str] | None = None,
) -> Callable[[str], Awaitable[list[dict]]]:
    """Return an async ``search(query)`` backed by the WebIQ SDK.

    ``client`` (an ``httpx.AsyncClient``) is passed to the SDK as-is when given —
    tests inject a ``MockTransport``-backed client with the WebIQ ``base_url``.
    When ``None`` the SDK constructs its own client against the live endpoint.
    ``key_reader`` overrides the Key Vault read in tests.
    """
    from webiq import WebIQAsyncClient
    from webiq.types import ContentFormat

    api_key = _resolve_api_key(key_reader)
    sdk = WebIQAsyncClient(api_key=api_key, http_client=client)
    limit = _max_results()

    async def search(query: str) -> list[dict]:
        response = await sdk.web.search(
            query, max_results=limit, content_format=ContentFormat.text
        )
        results: list[dict] = []
        for r in response.webResults or []:
            finding = (r.content or r.title or "").strip()
            url = (r.url or "").strip()
            if not finding and not url:
                continue
            results.append(
                {"finding": finding, "url": url, "confidence": _DEFAULT_CONFIDENCE}
            )
        return results[:limit]

    return search


__all__ = ["_build_webiq_search"]
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `uv run pytest feature-council/tests/agents/webiq/test_webiq_sdk.py -q`
Expected: PASS — but `test_foundry_provider_now_unsupported` will still FAIL until Task A3 (it calls `build_webiq_client_from_env`, which still routes `foundry` to the old module). That is expected; it passes after A3. Confirm the other 4 tests pass now.

- [ ] **Step 5: Commit**

```bash
git add feature-council/src/dsf/agents/webiq/webiq_sdk.py feature-council/tests/agents/webiq/test_webiq_sdk.py
git commit -m "feat: add WebIQ SDK web-search backend for the webiq agent"
```

---

### Task A3: Rewrite the provider switch in `client.py` (default → webiq, drop foundry)

**Files:**
- Modify: `feature-council/src/dsf/agents/webiq/client.py`

- [ ] **Step 1: Replace the module docstring + provider switch**

Replace the docstring (lines 1-14) — old:

```python
"""Live WebIQ web-search client for :class:`WebIqMcpBackend`.

Builds an async ``search(query) -> list[dict]`` callable backed by a real
web-research provider, selected with the ``WEBIQ_PROVIDER`` env var:

* ``foundry`` (default; aliases ``azure`` / ``bing``) — Azure AI Foundry's
  *Grounding with Bing Search* tool, so WebIQ researches through the product's own
  Azure AI Foundry resource. Implemented in :mod:`dsf.agents.webiq.foundry`.
* ``tavily`` — the third-party Tavily web-search API (opt-in). Built here via
  ``httpx`` and constructed from ``TAVILY_API_KEY``.

The Tavily path accepts an injected ``httpx.AsyncClient`` so tests can drive it
with ``httpx.MockTransport`` and never touch the network.
"""
```

new:

```python
"""Live WebIQ web-search client for :class:`WebIqMcpBackend`.

Builds an async ``search(query) -> list[dict]`` callable backed by a real
web-research provider, selected with the ``WEBIQ_PROVIDER`` env var:

* ``webiq`` (default) — the Microsoft **WebIQ** SDK (API-key auth). Implemented
  in :mod:`dsf.agents.webiq.webiq_sdk`.
* ``tavily`` — the third-party Tavily web-search API (opt-in). Built here via
  ``httpx`` and constructed from ``TAVILY_API_KEY``.

Both paths accept an injected ``httpx.AsyncClient`` so tests can drive them with
``httpx.MockTransport`` and never touch the network. Any other provider value
raises :class:`NotImplementedError`.
"""
```

- [ ] **Step 2: Replace the constant + builder**

Replace lines 28-58 — old:

```python
_TAVILY_URL = "https://api.tavily.com/search"

#: ``WEBIQ_PROVIDER`` values that select the Azure AI Foundry grounding backend.
_FOUNDRY_PROVIDERS = frozenset({"foundry", "azure", "bing"})


def build_webiq_client_from_env(
    client: httpx.AsyncClient | None = None,
) -> Callable[[str], Awaitable[list[dict]]]:
    """Return an async ``search(query)`` backed by the configured provider.

    ``WEBIQ_PROVIDER`` (default ``foundry``) selects the backend:

    * ``foundry`` / ``azure`` / ``bing`` — Azure AI Foundry Grounding with Bing
      Search (see :func:`dsf.agents.webiq.foundry.build_foundry_search_from_env`).
      The ``client`` argument is ignored for this provider.
    * ``tavily`` — the Tavily web-search API (requires ``TAVILY_API_KEY``).

    Any other value raises :class:`NotImplementedError`.
    """
    provider = (os.environ.get("WEBIQ_PROVIDER") or "foundry").strip().lower()
    if provider in _FOUNDRY_PROVIDERS:
        from dsf.agents.webiq.foundry import build_foundry_search_from_env

        return build_foundry_search_from_env()
    if provider != "tavily":
        raise NotImplementedError(
            f"WEBIQ_PROVIDER {provider!r} not supported "
            "(use 'foundry' (default) or 'tavily')"
        )
    return _build_tavily_search_from_env(client)
```

new:

```python
_TAVILY_URL = "https://api.tavily.com/search"


def build_webiq_client_from_env(
    client: httpx.AsyncClient | None = None,
    key_reader: Callable[[str, str], str] | None = None,
) -> Callable[[str], Awaitable[list[dict]]]:
    """Return an async ``search(query)`` backed by the configured provider.

    ``WEBIQ_PROVIDER`` (default ``webiq``) selects the backend:

    * ``webiq`` — the Microsoft WebIQ SDK (see
      :func:`dsf.agents.webiq.webiq_sdk._build_webiq_search`). ``key_reader``
      overrides the Key Vault read in tests.
    * ``tavily`` — the Tavily web-search API (requires ``TAVILY_API_KEY``).
      ``key_reader`` is ignored for this provider.

    Any other value raises :class:`NotImplementedError`.
    """
    provider = (os.environ.get("WEBIQ_PROVIDER") or "webiq").strip().lower()
    if provider == "webiq":
        from dsf.agents.webiq.webiq_sdk import _build_webiq_search

        return _build_webiq_search(client=client, key_reader=key_reader)
    if provider != "tavily":
        raise NotImplementedError(
            f"WEBIQ_PROVIDER {provider!r} not supported "
            "(use 'webiq' (default) or 'tavily')"
        )
    return _build_tavily_search_from_env(client)
```

Note: `Callable` is already imported under `TYPE_CHECKING` (line 26) — the new `key_reader: Callable[...]` annotation is a string (`from __future__ import annotations` at line 16), so no import change is needed.

- [ ] **Step 3: Run the new SDK tests (now all pass) + the existing tavily/live tests**

Run: `uv run pytest feature-council/tests/agents/webiq/test_webiq_sdk.py feature-council/tests/agents/webiq/test_webiq_live.py -q`
Expected: PASS — all of `test_webiq_sdk.py` (incl. `test_foundry_provider_now_unsupported`) and the unchanged tavily/agent-selection tests in `test_webiq_live.py` (their autouse fixture sets `WEBIQ_PROVIDER=tavily`).

- [ ] **Step 4: Commit**

```bash
git add feature-council/src/dsf/agents/webiq/client.py
git commit -m "feat: default the webiq agent to the WebIQ SDK provider"
```

---

### Task A4: Delete `foundry.py` + its test; refresh `main.py` + README

**Files:**
- Delete: `feature-council/src/dsf/agents/webiq/foundry.py`
- Delete: `feature-council/tests/agents/webiq/test_webiq_foundry.py`
- Modify: `feature-council/src/dsf/agents/webiq/main.py`
- Modify: `feature-council/src/dsf/agents/webiq/README.md`

- [ ] **Step 1: Delete the Foundry module + test**

```bash
git rm feature-council/src/dsf/agents/webiq/foundry.py feature-council/tests/agents/webiq/test_webiq_foundry.py
```

- [ ] **Step 2: Update `main.py` docstrings (no behavior change)**

In `feature-council/src/dsf/agents/webiq/main.py`, replace the module docstring body (lines 4-7) — old:

```python
(:class:`dsf.agents.webiq.backend.WebIqMcpBackend`) is wired to the provider
client built from env vars — Azure AI Foundry Grounding with Bing Search by
default, Tavily optional. Otherwise the fixture-backed backend is used.
"""
```

new:

```python
(:class:`dsf.agents.webiq.backend.WebIqMcpBackend`) is wired to the provider
client built from env vars — the Microsoft WebIQ SDK by default, Tavily
optional. Otherwise the fixture-backed backend is used.
"""
```

And replace the `build_agent` docstring lines 22-25 — old:

```python
    In live mode (``DSF_MODE`` set to anything but ``local``, or ``mode``
    explicitly live) the real web-search backend is wired to the provider client
    built from env vars (Azure AI Foundry Grounding with Bing Search by default;
    Tavily optional). Otherwise the deterministic fixture-backed backend is used.
```

new:

```python
    In live mode (``DSF_MODE`` set to anything but ``local``, or ``mode``
    explicitly live) the real web-search backend is wired to the provider client
    built from env vars (the Microsoft WebIQ SDK by default; Tavily optional).
    Otherwise the deterministic fixture-backed backend is used.
```

- [ ] **Step 3: Rewrite the README provider table**

Replace `feature-council/src/dsf/agents/webiq/README.md` lines 13-18 — old:

```markdown
## Environment variables (live mode)

| Variable | Required | Default | Purpose |
| --- | --- | --- | --- |
| `WEBIQ_PROVIDER` | no | `tavily` | Search provider. Only `tavily` is supported; any other value raises. |
| `TAVILY_API_KEY` | yes (for `tavily`) | — | Tavily API key. |
```

new:

```markdown
## Environment variables (live mode)

| Variable | Required | Default | Purpose |
| --- | --- | --- | --- |
| `WEBIQ_PROVIDER` | no | `webiq` | Search provider: `webiq` (Microsoft WebIQ SDK) or `tavily`. Any other value raises. |
| `WEBIQ_API_KEY` | no | — | WebIQ API key override. When unset, the key is read from Key Vault. |
| `WEBIQ_API_KEY_SECRET` | no | `webiq-api-key` | Key Vault secret name holding the WebIQ API key. |
| `AZURE_KEYVAULT_URI` | yes (for `webiq`, when `WEBIQ_API_KEY` unset) | — | Product Key Vault URI the API key is read from. |
| `WEBIQ_MAX_RESULTS` | no | `5` | Max web results per query. |
| `TAVILY_API_KEY` | yes (for `tavily`) | — | Tavily API key. |
```

- [ ] **Step 4: Run the full webiq agent test dir + import-linter**

Run: `uv run pytest feature-council/tests/agents/webiq/ -q && uv run lint-imports`
Expected: PASS; import-linter `4 kept, 0 broken` (feature-council may import core; no new cross-member edges).

- [ ] **Step 5: Commit**

```bash
git add -A feature-council/src/dsf/agents/webiq/ feature-council/tests/agents/webiq/
git commit -m "refactor: remove the Bing/Foundry provider from the webiq agent"
```

---

## Phase B — Provisioner: seed the key, delete all Bing code (cli)

### Task B1: Replace `_BING_*` constants/helper with seed constants

**Files:**
- Modify: `cli/src/dsf/instance/provisioner.py`

- [ ] **Step 1: Add the `datetime` import**

In `cli/src/dsf/instance/provisioner.py`, after `import base64` (line 10) add:

```python
from datetime import datetime, timedelta, timezone
```

(Insert it in import order — after `import time` at line 16 is also fine; keep ruff's `I` rule happy by placing it among the stdlib imports, e.g. right after `import base64`. Ruff will reorder on `--fix` if needed.)

- [ ] **Step 2: Remove `import re`**

Delete line 12 `import re` (its only use, `_TRANSIENT_BING_STATUS`, is removed in step 3).

- [ ] **Step 3: Replace the Bing constants with seed constants**

Replace lines 82-98 — old:

```python
_BING_CONNECT_MAX_ATTEMPTS = 20
_BING_CONNECT_RETRY_DELAY = 30.0
_TRANSIENT_BING_STATUS = re.compile(r"\b(429|500|502|503|504)\b")
_TRANSIENT_BING_TOKENS = (
    "serviceerror",
    "internalservererror",
    "internal server error",
    "serviceunavailable",
    "service unavailable",
    "toomanyrequests",
    "too many requests",
    "bad gateway",
    "gateway timeout",
    "temporarily unavailable",
    "connection reset",
    "timed out",
)
```

new:

```python
#: Key Vault secret holding the WebIQ API key (owner + product vaults share the
#: name; the bicep container env points the agent at it via WEBIQ_API_KEY_SECRET).
_WEBIQ_KEY_SECRET = "webiq-api-key"
#: The product Key Vaults inherit the same MG-scoped Azure Policy as the owner
#: vault: secret writes must set a content type and an expiry. 30 days is the
#: verified-acceptable window; the secret is re-seeded on every `dsf new`.
_KV_SECRET_CONTENT_TYPE = "text/plain"
_KV_SECRET_EXPIRY_DAYS = 30


def _kv_secret_expiry_utc() -> str:
    """ISO-8601 UTC expiry stamp satisfying the Key Vault secret-expiry policy."""
    expiry = datetime.now(timezone.utc) + timedelta(days=_KV_SECRET_EXPIRY_DAYS)
    return expiry.strftime("%Y-%m-%dT%H:%M:%SZ")
```

- [ ] **Step 4: Remove `_is_transient_bing_error`**

Delete the whole `_is_transient_bing_error` function (lines 162-173). View it first to get the exact block:

Run: `uv run python - <<'PY'\nimport pathlib,re\np=pathlib.Path("cli/src/dsf/instance/provisioner.py")\nPY`
(Or just open the file.) Delete from `def _is_transient_bing_error(` through its final `return` line (the function ends just before the next `def`/`class`). It is referenced only at line 923, removed in Task B3.

- [ ] **Step 5: Run ruff to confirm no syntax/import breakage yet**

Run: `uv run ruff check cli/src/dsf/instance/provisioner.py`
Expected: may report `_kv_secret_expiry_utc` unused for now (used in B3) — acceptable mid-task. No syntax errors. (If ruff's unused-import check flags `datetime`, it is used by the helper, so it will not.)

- [ ] **Step 6: Commit**

```bash
git add cli/src/dsf/instance/provisioner.py
git commit -m "refactor: replace Bing-connect constants with KV seed constants"
```

---

### Task B2: Swap the plan step, execute branch, and provision-command param

**Files:**
- Modify: `cli/src/dsf/instance/provisioner.py`
- Test: `cli/tests/instance/test_provisioner.py`

- [ ] **Step 1: Update the plan-step-name test (write the failing test first)**

In `cli/tests/instance/test_provisioner.py`, in `test_plan_step_order_and_names` replace the list entry `"connect_bing_grounding",` (line 128) with `"seed_webiq_key",`. The order becomes:

```python
        "create_resource_group",
        "provision_azure",
        "seed_appconfig",
        "seed_app_key",
        "seed_webiq_key",
        "register_product",
```

i.e. remove `"connect_bing_grounding"` from after `provision_azure`, and insert `"seed_webiq_key"` immediately after `"seed_app_key"` (line 130).

- [ ] **Step 2: Run it to verify it fails**

Run: `uv run pytest cli/tests/instance/test_provisioner.py::test_plan_step_order_and_names -q`
Expected: FAIL — the plan still emits `connect_bing_grounding`, not `seed_webiq_key`.

- [ ] **Step 3: Replace the `connect_bing_grounding` plan step with `seed_webiq_key`**

In `provisioner.py`, replace the `ProvisionStep(name="connect_bing_grounding", ...)` block (lines 302-308) by **deleting** it, and after the `seed_app_key` `ProvisionStep` (ends line 322) **insert**:

```python
            ProvisionStep(
                name="seed_webiq_key",
                description=(
                    "Seed the WebIQ API key from the owner Key Vault into the "
                    f"product Key Vault for {s.product}"
                ),
            ),
```

- [ ] **Step 4: Replace the `connect_bing_grounding` execute branch**

Replace lines 458-465 — old:

```python
        elif step.name == "connect_bing_grounding":
            if not self.spec.enable_bing_grounding:
                step.result = "skipped (bing grounding disabled)"
            elif not execute:
                step.result = "connected (dry-run)"
            else:
                self._connect_bing_grounding(azure_result)
                step.executed, step.result = True, "connected"
```

new:

```python
        elif step.name == "seed_webiq_key":
            if not self._owner_keyvault_uri:
                step.result = "skipped (no owner App configured)"
            elif not execute:
                step.result = "seeded (dry-run)"
            else:
                self._seed_webiq_key(azure_result)
                step.executed, step.result = True, "seeded"
```

- [ ] **Step 5: Remove the `enableBingGrounding` provision-command param**

In `_provision_azure_command`, delete line 747:

```python
            f"enableBingGrounding={'true' if s.enable_bing_grounding else 'false'}",
```

- [ ] **Step 6: Run the plan-order test (passes); commit after B3 implements the method**

Run: `uv run pytest cli/tests/instance/test_provisioner.py::test_plan_step_order_and_names -q`
Expected: PASS. (`_seed_webiq_key` is referenced but only called on the execute path, added in B3. Do not commit until B3 so the tree stays runnable.)

---

### Task B3: Add `_seed_webiq_key`, harden both seeds for the KV policy, delete `_connect_bing_grounding`

**Files:**
- Modify: `cli/src/dsf/instance/provisioner.py`
- Test: `cli/tests/instance/test_provisioner.py`

- [ ] **Step 1: Write the failing tests**

In `cli/tests/instance/test_provisioner.py`, add these tests (near the existing `test_seed_app_key_*` tests, ~line 1198):

```python
def test_seed_webiq_key_copies_owner_secret_into_product_vault(tmp_path):
    spec = InstanceSpec(product="demo", owner="acme")
    calls: list[list[str]] = []

    def fake_run(cmd, **kwargs):
        calls.append(cmd)
        return MagicMock(returncode=0, stdout="wq-secret\n")

    prov = InstanceProvisioner(
        spec, run=fake_run, repo_root=tmp_path, sleep=lambda _s: None,
        owner_keyvault_uri="https://kv-dsf-app.vault.azure.net/",
    )
    prov._seed_webiq_key(_azure_result_with(tmp_path, keyVaultName="kv-demo-xyz"))

    show = next(c for c in calls if c[:4] == ["az", "keyvault", "secret", "show"])
    assert "kv-dsf-app" in show and "webiq-api-key" in show  # read from owner vault
    setc = next(c for c in calls if c[:4] == ["az", "keyvault", "secret", "set"])
    assert "kv-demo-xyz" in setc and "webiq-api-key" in setc
    assert "--file" in setc                       # value pushed via a temp file
    assert "--content-type" in setc and "text/plain" in setc
    assert "--expires" in setc                    # satisfies the MG expiry policy
    # the secret value is never passed on argv
    assert not any("wq-secret" in arg for c in calls for arg in c)


def test_seed_webiq_key_raises_without_owner_keyvault(tmp_path):
    import pytest

    prov = InstanceProvisioner(InstanceSpec(product="demo", owner="acme"), repo_root=tmp_path)
    with pytest.raises(RuntimeError, match="owner Key Vault"):
        prov._seed_webiq_key(_azure_result_with(tmp_path, keyVaultName="kv-demo-xyz"))


def test_seed_app_key_sets_content_type_and_expiry(tmp_path):
    spec = InstanceSpec(product="demo", owner="acme")
    calls: list[list[str]] = []

    def fake_run(cmd, **kwargs):
        calls.append(cmd)

        class R:
            returncode = 0
            stdout = "555\n" if "/repos/" in " ".join(cmd) else "-----PEM-----\n"

        return R()

    prov = InstanceProvisioner(
        spec, run=fake_run, repo_root=tmp_path, sleep=lambda _s: None,
        owner_keyvault_uri="https://kv-dsf-app.vault.azure.net/",
        github_app_id="42", github_installation_id="9001",
    )
    prov._seed_app_key(_azure_result_with(tmp_path, keyVaultName="kv-demo-xyz"))

    setc = next(c for c in calls if c[:4] == ["az", "keyvault", "secret", "set"])
    assert "--content-type" in setc and "--expires" in setc
```

- [ ] **Step 2: Run them to verify they fail**

Run: `uv run pytest cli/tests/instance/test_provisioner.py -k "seed_webiq_key or seed_app_key_sets_content_type" -q`
Expected: FAIL — `_seed_webiq_key` undefined; `_seed_app_key` set command lacks `--content-type`/`--expires`.

- [ ] **Step 3: Delete `_connect_bing_grounding`**

In `provisioner.py`, delete the entire `_connect_bing_grounding` method (lines 851-930, from `def _connect_bing_grounding(` through its `finally:` block ending `Path(body_path).unlink(missing_ok=True)`).

- [ ] **Step 4: Harden `_seed_app_key`'s set command**

In `_seed_app_key`, replace the `cmd = [...]` (the set command, lines 965-968) — old:

```python
            cmd = [
                "az", "keyvault", "secret", "set", "--vault-name", product_kv,
                "--name", "github-app-private-key", "--file", pem_path,
            ]
```

new:

```python
            cmd = [
                "az", "keyvault", "secret", "set", "--vault-name", product_kv,
                "--name", "github-app-private-key", "--file", pem_path,
                "--content-type", _KV_SECRET_CONTENT_TYPE,
                "--expires", _kv_secret_expiry_utc(),
            ]
```

- [ ] **Step 5: Add `_seed_webiq_key` (mirror `_seed_app_key`)**

Immediately after `_seed_app_key` (after its `finally:`/`unlink`, the old line 981), insert:

```python
    def _seed_webiq_key(self, azure_result: AzureProvisionResult | None) -> None:
        """Copy the WebIQ API key from the owner KV into the product KV (with retry).

        Mirrors :meth:`_seed_app_key`: the product Key Vault's role grant and this
        data-plane write race, so the (idempotent) set is retried until the grant
        propagates. The key is materialized to a 0600 temp file (stripped of any
        trailing newline so the API key is exact) only long enough to push it in,
        then unlinked; both the owner read and product write capture output so the
        key is never echoed. The write sets a content type + expiry to satisfy the
        Key Vault secret policy.
        """
        if not self._owner_keyvault_uri:
            raise RuntimeError(
                "no owner Key Vault configured; set DSF_OWNER_KEYVAULT_URI or "
                "--owner-keyvault-uri (run `dsf bootstrap` first)"
            )
        product_kv = azure_result.outputs.get("keyVaultName", "") if azure_result else ""
        if not product_kv:
            raise RuntimeError("provision_azure returned no keyVaultName; cannot seed WebIQ key")
        owner_kv = self._owner_keyvault_uri.split("//", 1)[-1].split(".", 1)[0]

        secret = self._run(
            [
                "az", "keyvault", "secret", "show", "--vault-name", owner_kv,
                "--name", _WEBIQ_KEY_SECRET, "--query", "value", "-o", "tsv",
            ],
            check=True,
            capture_output=True,
            text=True,
        )
        fd, key_path = tempfile.mkstemp(prefix="dsf-webiq-", suffix=".txt")
        try:
            with open(fd, "w", encoding="utf-8") as fh:
                fh.write(getattr(secret, "stdout", "").strip())
            Path(key_path).chmod(0o600)
            cmd = [
                "az", "keyvault", "secret", "set", "--vault-name", product_kv,
                "--name", _WEBIQ_KEY_SECRET, "--file", key_path,
                "--content-type", _KV_SECRET_CONTENT_TYPE,
                "--expires", _kv_secret_expiry_utc(),
            ]
            last_error: subprocess.CalledProcessError | None = None
            for attempt in range(1, _SEED_MAX_ATTEMPTS + 1):
                try:
                    self._run(cmd, check=True, capture_output=True, text=True)
                    return
                except subprocess.CalledProcessError as exc:
                    last_error = exc
                    if attempt < _SEED_MAX_ATTEMPTS:
                        self._sleep(_SEED_RETRY_DELAY)
            assert last_error is not None
            raise last_error
        finally:
            Path(key_path).unlink(missing_ok=True)
```

- [ ] **Step 6: Run the new tests + the existing seed tests**

Run: `uv run pytest cli/tests/instance/test_provisioner.py -k "seed_webiq_key or seed_app_key" -q`
Expected: PASS (new webiq tests, the content-type/expiry test, and the unchanged `test_seed_app_key_copies_owner_pem_into_product_vault` / `_raises_without_owner_keyvault`).

- [ ] **Step 7: Commit**

```bash
git add cli/src/dsf/instance/provisioner.py cli/tests/instance/test_provisioner.py
git commit -m "feat: seed the WebIQ API key into the product vault, drop Bing connect"
```

---

### Task B4: Purge the Bing fixtures + tests from `test_provisioner.py`

**Files:**
- Modify: `cli/tests/instance/test_provisioner.py`

- [ ] **Step 1: Remove the `_BING_*` constants + the `listKeys`/`connections` fake branches**

Delete the `_BING_CONNECTION_ID` … `_BING_OUTPUTS` block (lines 40-61). In `_az_deploy`, delete the two leading branches (lines 81-84):

```python
    if cmd[:2] == ["az", "rest"] and "/listKeys" in " ".join(cmd):
        return MagicMock(returncode=0, stdout=json.dumps({"key1": "bing-key"}))
    if cmd[:2] == ["az", "rest"] and "--url" in cmd and "connections/" in " ".join(cmd):
        return MagicMock(returncode=0, stdout="")
```

In `_AZURE_OUTPUTS_JSON` (lines 62-70) remove the `_BING_OUTPUTS` entry so it reads:

```python
_AZURE_OUTPUTS_JSON = (
    "{"
    + ",".join([
        _APPCONFIG_OUTPUT,
        _KEYVAULT_OUTPUT,
    ])
    + "}"
)
```

Then remove every other `+ _BING_OUTPUTS` concatenation. Find them:

Run: `grep -n "_BING_OUTPUTS\|_BING_" cli/tests/instance/test_provisioner.py`
For each remaining hit (the deployment-output JSON builders around the old lines 777/812/843/874/1001), drop the `+ _BING_OUTPUTS` so the dict closes cleanly, e.g. `'{"cosmosEndpoint": {"type": "String", "value": "x"}}'`.

- [ ] **Step 2: Delete the Bing-specific tests**

Delete these whole test functions:
- `test_provision_azure_enables_bing_grounding_by_default` (old 183-187)
- `test_provision_azure_disables_bing_grounding_when_spec_opts_out` (old 189-194)
- `test_plan_includes_connect_bing_grounding_after_provision_azure` (old 197-199)
- `test_connect_bing_grounding_puts_connection_with_retry` (old 1017-…)
- `test_connect_bing_grounding_skipped_when_disabled`
- `test_connect_bing_grounding_fails_fast_on_non_transient`

Run: `grep -n "def test.*bing\|def test_connect_bing\|enable_bing" cli/tests/instance/test_provisioner.py` to confirm none remain (the bicep structural test renames happen in Task D).

- [ ] **Step 3: Add a plan-includes-seed_webiq_key test**

Add (near the old `test_plan_includes_*`):

```python
def test_plan_includes_seed_webiq_key_after_seed_app_key():
    names = [s.name for s in InstanceProvisioner(_spec()).plan().steps]
    assert names[names.index("seed_app_key") + 1] == "seed_webiq_key"
    assert "connect_bing_grounding" not in names
```

- [ ] **Step 4: Run the full provisioner test module**

Run: `uv run pytest cli/tests/instance/test_provisioner.py -q`
Expected: PASS (bicep structural tests still reference Bing — they are fixed in Task D1; if you are running tasks strictly in order, expect the two bicep tests `test_main_bicep_has_no_inline_bing_connection` to still pass and `test_main_bicep_bing_connection_id_is_constructed` to still pass against the *current* bicep. They flip in Task D1.)

- [ ] **Step 5: Commit**

```bash
git add cli/tests/instance/test_provisioner.py
git commit -m "test: drop Bing-connect provisioner fixtures and tests"
```

---

## Phase C — Spec + CLI flag removal

### Task C1: Remove `enable_bing_grounding` from the spec model + CLI

**Files:**
- Modify: `cli/src/dsf/instance/spec.py:42`
- Modify: `cli/src/dsf/cli/factory.py:220` and `:431-439`
- Test: `cli/tests/cli/test_factory.py:45-63`

- [ ] **Step 1: Delete the failing CLI tests first**

In `cli/tests/cli/test_factory.py`, delete `test_new_parser_bing_grounding_default_and_opt_out` (lines 45-51) and `test_new_threads_bing_grounding_opt_out_into_spec` (lines 54-63).

- [ ] **Step 2: Remove the spec field**

In `cli/src/dsf/instance/spec.py`, delete line 42:

```python
    enable_bing_grounding: bool = True
```

- [ ] **Step 3: Remove the CLI argument + call-site**

In `cli/src/dsf/cli/factory.py`, delete the call-site line 220:

```python
        enable_bing_grounding=args.enable_bing_grounding,
```

and the argparse block (lines 431-439):

```python
    p_new.add_argument(
        "--enable-bing-grounding",
        action=argparse.BooleanOptionalAction,
        default=True,
        help="provision Grounding with Bing Search (a Foundry project + connection) for "
        "the WebIQ source agent; pass --no-enable-bing-grounding to skip it (e.g. the "
        "tenant blocks the Microsoft.Bing provider, or the Foundry connection deploy is "
        "flaky)",
    )
```

- [ ] **Step 4: Confirm `argparse` is still used**

Run: `grep -n "argparse" cli/src/dsf/cli/factory.py | head`
Expected: still imported/used elsewhere (e.g. `argparse.ArgumentParser`). If the only remaining reference were the deleted `BooleanOptionalAction`, ruff would flag the import — but `argparse.ArgumentParser` is used, so the import stays.

- [ ] **Step 5: Run the CLI + spec tests**

Run: `uv run pytest cli/tests/cli/test_factory.py cli/tests/instance/test_provisioner.py -q`
Expected: PASS. No reference to `enable_bing_grounding` remains: `grep -rn "enable_bing_grounding" cli/` returns nothing.

- [ ] **Step 6: Commit**

```bash
git add cli/src/dsf/instance/spec.py cli/src/dsf/cli/factory.py cli/tests/cli/test_factory.py
git commit -m "refactor: remove the --enable-bing-grounding flag and spec field"
```

---

## Phase D — Infra + runtime render

### Task D1: Strip Bing from `infra/main.bicep`; set WebIQ env

**Files:**
- Modify: `infra/main.bicep`
- Test: `cli/tests/instance/test_provisioner.py` (bicep structural tests)

> **Bicep guardrails:** before editing, read `.github/instructions/bicep-code-best-practices.instructions.md`. Keep `az bicep build` clean.

- [ ] **Step 1: Rewrite the bicep structural tests first (TDD)**

In `cli/tests/instance/test_provisioner.py`, replace `test_main_bicep_bing_connection_id_is_constructed` (old lines 1662-1677) with:

```python
def test_main_bicep_has_no_bing_resources_or_params():
    bicep = (_default_repo_root() / "infra" / "main.bicep").read_text()
    assert "Microsoft.Bing/accounts" not in bicep
    assert "enableBingGrounding" not in bicep
    assert "bingConnectionResourceId" not in bicep
    assert "WEBIQ_BING_CONNECTION_ID" not in bicep
    assert "aiProjectEndpoint" not in bicep
    assert "AZURE_AI_PROJECT_ENDPOINT" not in bicep
    # the Foundry account (Azure OpenAI) and its deployments stay
    assert "Microsoft.CognitiveServices/accounts@" in bicep


def test_main_bicep_sets_webiq_provider_env():
    bicep = (_default_repo_root() / "infra" / "main.bicep").read_text()
    assert "{ name: 'WEBIQ_PROVIDER', value: 'webiq' }" in bicep
    assert "{ name: 'WEBIQ_API_KEY_SECRET', value: 'webiq-api-key' }" in bicep
```

Keep `test_main_bicep_has_no_inline_bing_connection` (still valid — it asserts the `projects/connections` resource is absent) and `test_main_bicep_orchestrator_app_uses_bounded_name_prefix` unchanged.

- [ ] **Step 2: Run the bicep tests to verify the new ones fail**

Run: `uv run pytest cli/tests/instance/test_provisioner.py -k "main_bicep" -q`
Expected: FAIL — current bicep still contains `Microsoft.Bing/accounts`, `enableBingGrounding`, `WEBIQ_PROVIDER='foundry'`, etc.

- [ ] **Step 3: Remove the two Bing params**

Delete lines 82-86:

```python
@description('Provision Grounding with Bing Search (Microsoft.Bing/accounts) plus a Foundry project + connection so the WebIQ source agent can research the web. Set false where the tenant policy blocks the Microsoft.Bing provider.')
param enableBingGrounding bool = true

@description('SKU (tier) for the Grounding with Bing Search account.')
param bingGroundingSkuName string = 'G1'
```

- [ ] **Step 4: Remove the Bing vars**

Replace lines 112-118 — old:

```python
// Foundry project that hosts the Grounding with Bing Search connection. The
// azure-ai-agents BingGroundingTool the WebIQ agent uses targets this project
// endpoint (AZURE_AI_PROJECT_ENDPOINT) and resolves the connection by id.
var aiProjectName = '${namePrefix}-proj-${suffix}'
var bingConnectionName = '${namePrefix}-bing-conn-${suffix}'
var bingConnectionResourceId = enableBingGrounding ? resourceId('Microsoft.CognitiveServices/accounts/projects/connections', '${namePrefix}-aif-${suffix}', aiProjectName, bingConnectionName) : ''
var aiProjectEndpoint = enableBingGrounding ? 'https://${namePrefix}-aif-${suffix}.services.ai.azure.com/api/projects/${aiProjectName}' : ''
```

new: *(delete the block entirely — none of these are used anymore)*

```python
```

- [ ] **Step 5: Neutralize the `allowProjectManagement` comment**

Replace lines 316-317 — old:

```python
    // Enable the Foundry project sub-resource that hosts the Bing grounding connection.
    allowProjectManagement: true
```

new:

```python
    // Foundry account management surface (kept enabled; no child projects today).
    allowProjectManagement: true
```

- [ ] **Step 6: Delete the Bing account, Foundry project, and project role assignment**

Delete lines 367-410 (the entire commented Bing section through the `foundryAgentsUserAssignment` resource). That is: the `// ---- Grounding with Bing Search ----` comment block, `resource bingAccount ... { ... }`, `resource aiProject ... { ... }`, and `resource foundryAgentsUserAssignment ... { ... }`. Keep `foundryOpenAIUserAssignment` (lines 356-365) — the runtime still needs Azure OpenAI access.

- [ ] **Step 7: Fix the container-app env block**

Replace lines 470-472 — old:

```python
            { name: 'AZURE_AI_PROJECT_ENDPOINT', value: aiProjectEndpoint }
            { name: 'WEBIQ_BING_CONNECTION_ID', value: bingConnectionResourceId }
            { name: 'WEBIQ_PROVIDER', value: 'foundry' }
```

new:

```python
            { name: 'WEBIQ_PROVIDER', value: 'webiq' }
            { name: 'WEBIQ_API_KEY_SECRET', value: 'webiq-api-key' }
```

(`AZURE_KEYVAULT_URI` remains set in this block — the agent reads the key from it.)

- [ ] **Step 8: Delete the Bing outputs**

Delete lines 532-542:

```python
@description('Foundry project endpoint the WebIQ source agent calls (empty when Bing grounding is disabled).')
output aiProjectEndpoint string = aiProjectEndpoint

@description('Grounding with Bing Search connection id for WEBIQ_BING_CONNECTION_ID (empty when disabled).')
output bingConnectionId string = bingConnectionResourceId

@description('Grounding with Bing Search account resource id (Microsoft.Bing/accounts) for the out-of-band connection step (empty when disabled).')
output bingAccountId string = enableBingGrounding ? bingAccount.id : ''

@description('Grounding with Bing Search account endpoint used as the connection target (empty when disabled).')
output bingAccountEndpoint string = enableBingGrounding ? bingAccount!.properties.endpoint : ''
```

- [ ] **Step 9: Build the bicep + run the bicep tests**

Run: `az bicep build --file infra/main.bicep --stdout > /dev/null && echo BICEP_OK`
Expected: `BICEP_OK` with no errors (the prior BCP081 Microsoft.Bing warning is gone too).
Run: `uv run pytest cli/tests/instance/test_provisioner.py -k "main_bicep" -q`
Expected: PASS.

- [ ] **Step 10: Commit**

```bash
git add infra/main.bicep cli/tests/instance/test_provisioner.py
git commit -m "feat: remove Bing grounding from infra; set WebIQ provider env"
```

---

### Task D2: Update `runtime_render` env map + the deploy-progress sample

**Files:**
- Modify: `cli/src/dsf/instance/runtime_render.py:36-37` and `:63-72`
- Modify: `cli/tests/instance/test_deploy_progress.py:225,244` (cosmetic)
- Test: `cli/tests/instance/` render tests (discover below)

- [ ] **Step 1: Find the render test that asserts the env file (write/adjust failing test)**

Run: `grep -rln "render_runtime_bundle\|_render_env\|env.orchestrator\|WEBIQ_PROVIDER" cli/tests/instance`
Open the matching test. Add (or extend an existing render assertion) a test that the rendered `.env.orchestrator`:
- contains `WEBIQ_PROVIDER=webiq` and `WEBIQ_API_KEY_SECRET=webiq-api-key`
- does **not** contain `WEBIQ_BING_CONNECTION_ID` or `AZURE_AI_PROJECT_ENDPOINT`

If no such test exists, add to `cli/tests/instance/test_runtime_render.py` (create if missing):

```python
from dsf.instance.runtime_render import _render_env


def test_render_env_has_webiq_provider_not_bing():
    env = _render_env("demo", {"keyVaultUri": "https://kv.vault.azure.net/"})
    assert "WEBIQ_PROVIDER=webiq" in env
    assert "WEBIQ_API_KEY_SECRET=webiq-api-key" in env
    assert "AZURE_KEYVAULT_URI=https://kv.vault.azure.net/" in env
    assert "WEBIQ_BING_CONNECTION_ID" not in env
    assert "AZURE_AI_PROJECT_ENDPOINT" not in env
```

- [ ] **Step 2: Run it to verify it fails**

Run: `uv run pytest cli/tests/instance/test_runtime_render.py -q`
Expected: FAIL — the map still emits the two Bing rows and no static WebIQ lines.

- [ ] **Step 3: Remove the Bing rows from `_ENDPOINT_MAP`**

Delete lines 36-37:

```python
    ("AZURE_AI_PROJECT_ENDPOINT", "aiProjectEndpoint"),
    ("WEBIQ_BING_CONNECTION_ID", "bingConnectionId"),
```

- [ ] **Step 4: Add the static WebIQ lines to `_render_env`**

Replace lines 69-71 — old:

```python
        f"DSF_PRODUCT={product}",
    ]
    lines.extend(f"{var}={outputs.get(key, '')}" for var, key in _ENDPOINT_MAP)
```

new:

```python
        f"DSF_PRODUCT={product}",
        "WEBIQ_PROVIDER=webiq",
        "WEBIQ_API_KEY_SECRET=webiq-api-key",
    ]
    lines.extend(f"{var}={outputs.get(key, '')}" for var, key in _ENDPOINT_MAP)
```

- [ ] **Step 5: Cosmetic — retire the `bing-conn` sample in the progress test**

In `cli/tests/instance/test_deploy_progress.py`, change the sample resource name (line 225) from `"aif/proj/bing-conn"` to a neutral one, e.g. `"appcs/store/config"`, and update the assertion on line 244 from `assert "bing-conn" in msg` to `assert "config" in msg`. (This test exercises the progress renderer, not Bing.)

- [ ] **Step 6: Run the render + deploy-progress tests**

Run: `uv run pytest cli/tests/instance/test_runtime_render.py cli/tests/instance/test_deploy_progress.py -q`
Expected: PASS.

- [ ] **Step 7: Commit**

```bash
git add cli/src/dsf/instance/runtime_render.py cli/tests/instance/
git commit -m "feat: render WebIQ provider env, drop Bing from runtime bundle"
```

---

## Phase E — Docs + ADR + spec reconciliation

### Task E1: ADR 0020

**Files:**
- Create: `docs/adr/0020-webiq-via-webiq-sdk.md`

- [ ] **Step 1: Read an existing ADR for the house format**

Run: `sed -n '1,40p' docs/adr/0019-rename-handoff-label-creation-ready.md`
Match its heading structure (Status / Context / Decision / Consequences) and length.

- [ ] **Step 2: Create the ADR**

Create `docs/adr/0020-webiq-via-webiq-sdk.md`:

```markdown
# 0020 — WebIQ source agent runs on the Microsoft WebIQ SDK

## Status

Accepted (2026-06-25). Supersedes the "Grounding with Bing Search" decision baked
into the WebIQ agent + `infra/main.bicep` (issue #85).

## Context

The WebIQ source agent gives greenfield products real web research before they
have telemetry. We first implemented it on Azure AI Foundry's *Grounding with
Bing Search*: a `Microsoft.Bing/accounts` resource exposed to the runtime through
a Foundry **project** connection, created inside the ARM deployment.

That connection proved unprovisionable on a cold Foundry account: the platform's
asynchronous managed-KV registration lags ARM's `Succeeded` on the account, so the
ApiKey-connection secret write returns HTTP 500, ARM never re-drives it, and the
deployment wedges ~10 minutes then fails. Serializing it later in the template and
creating it out-of-band with bounded retry both still lost the race on live cold
accounts.

Microsoft **WebIQ** (announced at Build) is a first-party web-research capability
with its own Python SDK (`pip install webiq`) and simple API-key auth, independent
of any per-product Foundry/Bing resource.

## Decision

The `webiq` agent's default provider is now `webiq`, backed by the real WebIQ SDK
(`dsf.agents.webiq.webiq_sdk`). Tavily stays selectable via `WEBIQ_PROVIDER=tavily`.
The Bing/Foundry-project provider is removed.

The API key is resolved app-side — `WEBIQ_API_KEY` env override, else read from the
product Key Vault (secret `webiq-api-key`) — mirroring the GitHub App private key.
`dsf new` seeds that secret from the central owner vault into the product vault
after the ARM deploy (`InstanceProvisioner._seed_webiq_key`). Product-vault writes
set a content type and a 30-day expiry to satisfy the tenant's MG-scoped Key Vault
secret policy; the secret is re-seeded on every `dsf new` / rotation.

`infra/main.bicep` drops the `Microsoft.Bing/accounts` resource, the Foundry
project + its connection and role assignment, the `enableBingGrounding` /
`bingGroundingSkuName` params, and the Bing/project outputs and container env. The
Foundry **account** (Azure OpenAI for chat + embeddings) is unchanged.

## Consequences

- Web research no longer depends on the flaky cold-account Bing connection; the
  agent talks to a stable first-party endpoint.
- A new third-party dependency (`webiq`) and a new secret to manage. The 30-day
  expiry means the key must be re-seeded (re-run `dsf new` or a rotation) before it
  lapses; lengthen it once the tenant's max-validity policy cap is confirmed.
- Tavily remains a drop-in fallback for environments without a WebIQ key.
```

- [ ] **Step 3: Commit**

```bash
git add docs/adr/0020-webiq-via-webiq-sdk.md
git commit -m "docs: ADR 0020 — WebIQ agent on the Microsoft WebIQ SDK"
```

---

### Task E2: Provisioning docs

**Files:**
- Modify: `docs/site/get-started/provision-a-factory.md`

- [ ] **Step 1: Find the Bing recovery note**

Run: `grep -n -i "bing\|grounding\|enable-bing" docs/site/get-started/provision-a-factory.md`

- [ ] **Step 2: Replace it**

Remove any "Grounding with Bing" / `--no-enable-bing-grounding` recovery admonition. If the page lists what `dsf new` provisions, replace the Bing line with: *"The WebIQ source agent's API key is seeded from the central owner vault into the product Key Vault (secret `webiq-api-key`); web research runs on the Microsoft WebIQ SDK (see ADR 0020)."* If there is nothing Bing-specific, make no change and note it in the commit body.

- [ ] **Step 3: Commit**

```bash
git add docs/site/get-started/provision-a-factory.md
git commit -m "docs: replace Bing-grounding provisioning note with WebIQ"
```

---

### Task E3: Reconcile the design spec with the two divergences

**Files:**
- Modify: `docs/superpowers/specs/2026-06-25-webiq-sdk-source-agent-design.md`

- [ ] **Step 1: Update the spec text**

In the infra section, change "keep the Foundry project" to "remove the Foundry project too (it is Bing-only)". In the provisioner section, add that the product-vault seed writes set `--content-type text/plain` + a 30-day `--expires` to satisfy the MG Key Vault policy, applied to both `_seed_webiq_key` and `_seed_app_key`.

- [ ] **Step 2: Commit**

```bash
git add docs/superpowers/specs/2026-06-25-webiq-sdk-source-agent-design.md
git commit -m "docs: reconcile WebIQ spec with project removal + seed expiry"
```

---

## Phase F — Full gates + cleanup

### Task F1: Run every gate, clean up scratch

- [ ] **Step 1: Full test suite**

Run: `uv run pytest -q`
Expected: all pass (1 pre-existing Starlette deprecation warning is fine). No `bing`/`foundry`/`enable_bing_grounding` references remain:
Run: `grep -rn -i "enable_bing_grounding\|connect_bing\|WEBIQ_BING\|build_foundry_search\|aiProjectEndpoint" cli/src feature-council/src infra/main.bicep` → expect no matches.

- [ ] **Step 2: Lint + import boundaries**

Run: `uv run ruff check . && uv run lint-imports`
Expected: `All checks passed!` and `Contracts: 4 kept, 0 broken`.

- [ ] **Step 3: Bicep build**

Run: `az bicep build --file infra/main.bicep --stdout > /dev/null && echo BICEP_OK`
Expected: `BICEP_OK`.

- [ ] **Step 4: Editable CLI reflects the new step**

Run: `uv run dsf new --product gate --owner acme --dry-run 2>&1 | grep -i "seed_webiq_key\|connect_bing" || true`
Expected: shows a `seed_webiq_key` step, no `connect_bing_grounding`.

- [ ] **Step 5: Clean up the scratch venv**

Run: `rm -rf /tmp/webiq-probe`

- [ ] **Step 6: Final review commit (if any stragglers)**

```bash
git status
# commit any remaining doc/test tidy-ups with an appropriate Conventional Commit prefix
```

---

## Self-Review

- **Spec coverage:** agent provider (A2/A3), key resolution + KV read (A2), provisioner seed (B3), Bing removal across provisioner/spec/CLI/infra/render (B/C/D), deps (A1), tests (every task), docs + ADR (E). Both spec divergences are tracked (Task E3) and implemented (project removal D1; seed expiry/content-type B3).
- **Type/name consistency:** `build_webiq_client_from_env(client, key_reader)` (A3) ↔ `_build_webiq_search(*, client, key_reader)` (A2). Provider key `"webiq"` is consistent across client default (A3), bicep env (D1), render (D2), README (A4). Secret name `webiq-api-key` consistent across `_WEBIQ_KEY_SECRET` (B1), bicep `WEBIQ_API_KEY_SECRET` (D1), render (D2), `_DEFAULT_KEY_SECRET` (A2). `_kv_secret_expiry_utc` / `_KV_SECRET_CONTENT_TYPE` used by both seeds (B3).
- **Placeholder scan:** none — every code step shows full code; deletions name exact symbols/line ranges.

---

## Open items to raise with the user (post-implementation)

1. **Push + history hygiene:** retire the now-moot Bing commits `503e328` (dependsOn) and `e6a8d16` (out-of-band Bing) when pushing `main`; keep `ed9129f` (orchestrator app-name fix). Decide squash vs forward-revert.
2. **Live cold-account validation:** run `dsf new` end-to-end and confirm `provision_azure` → `seed_webiq_key` both succeed and the agent answers a live `web.search`.
3. **Key expiry:** confirm the tenant's Key Vault max-validity cap and lengthen `_KV_SECRET_EXPIRY_DAYS` toward it so the seeded key does not lapse between provisions.
