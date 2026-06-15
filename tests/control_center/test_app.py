"""Control Center (Phase 7) — TestClient coverage for the write surface."""

from __future__ import annotations

import pytest
from fastapi.testclient import TestClient

from dsf.container import build_services
from dsf.control_center.app import create_app


@pytest.fixture
def services():
    """A fresh local service bundle per test (deterministic fakes)."""
    return build_services("local")


@pytest.fixture
def client(services):
    """TestClient over an app wired to the held ``services`` instance."""
    # redirect handling off so we can assert the 303 directly.
    return TestClient(create_app(services), follow_redirects=False)


def test_index_renders_flags(client):
    resp = client.get("/")
    assert resp.status_code == 200
    body = resp.text
    assert "text/html" in resp.headers["content-type"]
    # mentions at least one critic name and the dry-run switch.
    assert "grounding" in body
    assert "dry-run" in body.lower()


def test_api_state_has_seeded_flags(client):
    resp = client.get("/api/state")
    assert resp.status_code == 200
    data = resp.json()
    # snapshot reflects the seeded defaults.
    assert data["dry_run"] is True
    critic_names = {c["name"] for c in data["critics"]}
    assert "duplication" in critic_names
    assert all(c["enabled"] for c in data["critics"])
    agent_names = {a["name"] for a in data["agents"]}
    assert {"SENTRY", "GRAFANA", "FOUNDRYIQ", "WEBIQ", "TICKETS"} <= agent_names
    assert "_overrides" in data["snapshot"]


def test_toggle_disables_critic(services):
    client = TestClient(create_app(services), follow_redirects=False)
    assert services.config.is_enabled("critic.duplication") is True

    resp = client.post(
        "/toggle",
        data={"flag": "critic.duplication", "value": "false"},
    )
    assert resp.status_code == 303
    assert resp.headers["location"] == "/"
    # assert on the SAME services instance we hold.
    assert services.config.is_enabled("critic.duplication") is False


def test_toggle_dry_run_reflected_in_snapshot(services):
    client = TestClient(create_app(services), follow_redirects=False)
    resp = client.post("/toggle", data={"flag": "dry_run", "value": "false"})
    assert resp.status_code == 303
    assert services.config.is_enabled("dry_run") is False

    state = client.get("/api/state").json()
    assert state["dry_run"] is False


def test_set_value_updates_threshold(services):
    client = TestClient(create_app(services), follow_redirects=False)
    resp = client.post(
        "/set-value",
        data={"key": "default_threshold", "value": "0.8"},
    )
    assert resp.status_code == 303
    assert services.config.get_value("default_threshold") == pytest.approx(0.8)
