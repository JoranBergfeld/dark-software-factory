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
