"""WebIQ Foundry (Grounding with Bing Search) provider tests (no network).

Exercise the pure mapping from a grounded result to WebIQ result dicts via an
injected ``runner``, plus provider dispatch and live backend selection. The real
Azure AI Agents call is never made (it lives behind the injected seam / lazy
import).
"""

from __future__ import annotations

import pytest

from dsf.agents.webiq.backend import WebIqMcpBackend
from dsf.agents.webiq.client import build_webiq_client_from_env
from dsf.agents.webiq.foundry import (
    GroundingCitation,
    GroundingResult,
    build_foundry_search_from_env,
)
from dsf.agents.webiq.main import build_agent

_FOUNDRY_ENV = {
    "AZURE_AI_PROJECT_ENDPOINT": "https://r.services.ai.azure.com/api/projects/p",
    "WEBIQ_BING_CONNECTION_ID": "/subscriptions/s/.../connections/bing",
    "WEBIQ_FOUNDRY_MODEL": "gpt-4o",
}


@pytest.fixture(autouse=True)
def _clean_env(monkeypatch):
    for var in (
        "WEBIQ_PROVIDER",
        "WEBIQ_MAX_RESULTS",
        "AZURE_AI_PROJECT_ENDPOINT",
        "WEBIQ_BING_CONNECTION_ID",
        "WEBIQ_FOUNDRY_MODEL",
        "AZURE_OPENAI_DEPLOYMENT",
        "TAVILY_API_KEY",
    ):
        monkeypatch.delenv(var, raising=False)


def _set_foundry_env(monkeypatch) -> None:
    for key, value in _FOUNDRY_ENV.items():
        monkeypatch.setenv(key, value)


async def test_maps_citations_to_results():
    captured: list[str] = []

    async def runner(query: str) -> GroundingResult:
        captured.append(query)
        return GroundingResult(
            answer="synthesized answer",
            citations=[
                GroundingCitation(url="https://a.com/x", title="A", snippet="snip a"),
                GroundingCitation(url="https://b.com/y", title="B"),
            ],
        )

    search = build_foundry_search_from_env(runner=runner)
    out = await search("pet-clinic competitor feature")

    assert captured == ["pet-clinic competitor feature"]
    assert out == [
        {"finding": "snip a", "url": "https://a.com/x", "confidence": 0.6},
        {"finding": "B", "url": "https://b.com/y", "confidence": 0.6},
    ]


async def test_answer_only_fallback_when_no_citations():
    async def runner(_query: str) -> GroundingResult:
        return GroundingResult(answer="just an answer", citations=[])

    search = build_foundry_search_from_env(runner=runner)
    out = await search("q")

    assert out == [{"finding": "just an answer", "url": "", "confidence": 0.5}]


async def test_empty_grounding_yields_no_results():
    async def runner(_query: str) -> GroundingResult:
        return GroundingResult()

    search = build_foundry_search_from_env(runner=runner)
    assert await search("q") == []


async def test_max_results_caps_output(monkeypatch):
    monkeypatch.setenv("WEBIQ_MAX_RESULTS", "1")

    async def runner(_query: str) -> GroundingResult:
        return GroundingResult(
            citations=[
                GroundingCitation(url="https://a/1", snippet="one"),
                GroundingCitation(url="https://a/2", snippet="two"),
            ]
        )

    search = build_foundry_search_from_env(runner=runner)
    out = await search("q")
    assert out == [{"finding": "one", "url": "https://a/1", "confidence": 0.6}]


def test_dispatch_default_is_foundry(monkeypatch):
    _set_foundry_env(monkeypatch)
    # No WEBIQ_PROVIDER set -> defaults to foundry; building must not raise and
    # must not require Tavily creds.
    search = build_webiq_client_from_env()
    assert callable(search)


@pytest.mark.parametrize("provider", ["foundry", "azure", "bing", "FOUNDRY"])
def test_dispatch_foundry_aliases(monkeypatch, provider):
    monkeypatch.setenv("WEBIQ_PROVIDER", provider)
    _set_foundry_env(monkeypatch)
    assert callable(build_webiq_client_from_env())


def test_dispatch_foundry_missing_env_raises(monkeypatch):
    monkeypatch.setenv("WEBIQ_PROVIDER", "foundry")
    with pytest.raises(RuntimeError):
        build_webiq_client_from_env()


def test_foundry_model_falls_back_to_openai_deployment(monkeypatch):
    monkeypatch.setenv("AZURE_AI_PROJECT_ENDPOINT", _FOUNDRY_ENV["AZURE_AI_PROJECT_ENDPOINT"])
    monkeypatch.setenv("WEBIQ_BING_CONNECTION_ID", _FOUNDRY_ENV["WEBIQ_BING_CONNECTION_ID"])
    monkeypatch.setenv("AZURE_OPENAI_DEPLOYMENT", "gpt-4o")
    # WEBIQ_FOUNDRY_MODEL unset -> falls back to AZURE_OPENAI_DEPLOYMENT, no raise.
    assert callable(build_foundry_search_from_env())


def test_build_agent_live_uses_foundry_backend(monkeypatch):
    _set_foundry_env(monkeypatch)
    assert isinstance(build_agent(mode="live").backend, WebIqMcpBackend)


def test_build_agent_live_missing_env_raises(monkeypatch):
    with pytest.raises(RuntimeError):
        build_agent(mode="live")
