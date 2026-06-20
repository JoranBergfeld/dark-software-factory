"""Incidents recurrence aggregation + live GitHub client (no network)."""

from __future__ import annotations

import httpx
import pytest

from dsf.agents.incidents.backend import (
    IncidentsFixtureBackend,
    IncidentsGitHubBackend,
)
from dsf.agents.incidents.client import build_incidents_client_from_env
from dsf.agents.incidents.main import build_agent


def test_github_backend_requires_gh_call():
    with pytest.raises(RuntimeError, match="requires a gh_call client"):
        IncidentsGitHubBackend(gh_call=None)


async def test_recurring_signature_collapses_to_one_higher_confidence_item():
    async def fake_gh_call(scope: dict) -> list[dict]:
        return [
            {"title": "Checkout 5xx", "html_url": "https://gh/x/issues/1"},
            {"title": "checkout  5xx", "html_url": "https://gh/x/issues/2"},
            {"title": "CHECKOUT 5XX", "html_url": "https://gh/x/issues/7"},
            {"title": "Orders timeout", "html_url": "https://gh/x/issues/9"},
        ]

    backend = IncidentsGitHubBackend(gh_call=fake_gh_call)
    items = await backend.gather({"product_hints": ["microbi"]})

    by_claim = {i.claim: i for i in items}
    assert len(items) == 2
    recurring = next(i for i in items if "recurred 3 times" in i.claim)
    oneoff = next(i for i in items if "filed once" in i.claim)
    assert recurring.confidence > oneoff.confidence
    assert recurring.product_hints == ["microbi"]
    # Citation points at the first issue in the group.
    assert recurring.raw_citation == "https://gh/x/issues/1"
    assert by_claim  # at least the two grouped claims exist


async def test_one_off_incident_scores_below_default_bar():
    async def fake_gh_call(scope: dict) -> list[dict]:
        return [{"title": "Single blip", "html_url": "https://gh/x/issues/3"}]

    backend = IncidentsGitHubBackend(gh_call=fake_gh_call)
    items = await backend.gather({})
    assert len(items) == 1
    assert items[0].confidence < 0.6


@pytest.fixture(autouse=True)
def _env(monkeypatch):
    monkeypatch.setenv("GITHUB_TOKEN", "tok")
    monkeypatch.setenv("GITHUB_REPO", "example/microbi")


async def test_client_lists_incident_issues_and_drops_prs():
    captured: dict = {}

    def handler(request: httpx.Request) -> httpx.Response:
        captured["path"] = request.url.path
        captured["query"] = request.url.query.decode()
        captured["auth"] = request.headers.get("Authorization")
        return httpx.Response(
            200,
            json=[
                {"title": "Checkout 5xx", "html_url": "https://gh/x/issues/1", "number": 1},
                {
                    "title": "A PR",
                    "html_url": "https://gh/x/pull/2",
                    "number": 2,
                    "pull_request": {"url": "..."},
                },
            ],
        )

    client = httpx.AsyncClient(
        transport=httpx.MockTransport(handler),
        base_url="https://api.github.com",
        headers={"Authorization": "Bearer tok"},
    )
    gh_call = build_incidents_client_from_env(client=client)
    rows = await gh_call({"github_repo": "example/microbi"})

    assert captured["path"] == "/repos/example/microbi/issues"
    assert "labels=incident" in captured["query"]
    assert "state=open" in captured["query"]
    assert captured["auth"] == "Bearer tok"
    assert len(rows) == 1
    assert rows[0]["title"] == "Checkout 5xx"


def test_build_agent_local_uses_fixture(monkeypatch):
    monkeypatch.delenv("DSF_MODE", raising=False)
    assert isinstance(build_agent(mode="local").backend, IncidentsFixtureBackend)


def test_build_agent_live_uses_github(monkeypatch):
    monkeypatch.setenv("GITHUB_TOKEN", "live-tok")
    monkeypatch.setenv("GITHUB_REPO", "example/microbi")
    assert isinstance(build_agent(mode="live").backend, IncidentsGitHubBackend)


def test_build_agent_live_missing_env_raises(monkeypatch):
    monkeypatch.delenv("GITHUB_TOKEN", raising=False)
    monkeypatch.delenv("GITHUB_REPO", raising=False)
    with pytest.raises(RuntimeError):
        build_agent(mode="live")
