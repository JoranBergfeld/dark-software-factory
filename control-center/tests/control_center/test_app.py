"""Control Center (Phase 7) -- TestClient coverage for the write surface."""

from __future__ import annotations

import pytest
from fastapi.testclient import TestClient

from dsf.control_center.app import create_app
from dsf_testing import build_test_services

# ---------------------------------------------------------------------------
# Fixtures
# ---------------------------------------------------------------------------

_TOKEN = "test-operator-token"


@pytest.fixture
def services():
    """A fresh local service bundle per test (deterministic fakes)."""
    return build_test_services()


@pytest.fixture
def client(services):
    """Unauthenticated TestClient in open (local) mode -- no token."""
    return TestClient(create_app(services), follow_redirects=False)


@pytest.fixture
def auth_client(services):
    """TestClient with bearer auth enforced."""
    return TestClient(create_app(services, token=_TOKEN), follow_redirects=False)


def _csrf(c: TestClient) -> str:
    """Fetch the CSRF token by loading the index page."""
    resp = c.get("/")
    assert resp.status_code == 200
    return resp.cookies.get("cc_csrf", "")


# ---------------------------------------------------------------------------
# Read routes -- always open
# ---------------------------------------------------------------------------


def test_index_renders_flags(client):
    resp = client.get("/")
    assert resp.status_code == 200
    body = resp.text
    assert "text/html" in resp.headers["content-type"]
    assert "grounding" in body


def test_api_state_has_seeded_flags(client):
    resp = client.get("/api/state")
    assert resp.status_code == 200
    data = resp.json()
    critic_names = {c["name"] for c in data["critics"]}
    assert "duplication" in critic_names
    assert all(c["enabled"] for c in data["critics"])
    agent_names = {a["name"] for a in data["agents"]}
    assert {"SENTRY", "GRAFANA", "FOUNDRYIQ", "WEBIQ"} <= agent_names
    assert "_overrides" in data["snapshot"]


# ---------------------------------------------------------------------------
# Issue #7 -- unauthenticated writes must return 401
# ---------------------------------------------------------------------------


def test_unauthenticated_toggle_returns_401(auth_client):
    """POST /toggle without a bearer token must be rejected with 401."""
    resp = auth_client.post(
        "/toggle",
        data={"flag": "dry_run", "value": "false"},
    )
    assert resp.status_code == 401


def test_unauthenticated_set_value_returns_401(auth_client):
    """POST /set-value without a bearer token must be rejected with 401."""
    resp = auth_client.post(
        "/set-value",
        data={"key": "default_threshold", "value": "0.8"},
    )
    assert resp.status_code == 401


def test_wrong_token_returns_401(auth_client):
    """A wrong bearer token must be rejected."""
    resp = auth_client.post(
        "/toggle",
        data={"flag": "dry_run", "value": "false"},
        headers={"Authorization": "Bearer wrong-token"},
    )
    assert resp.status_code == 401


# ---------------------------------------------------------------------------
# Authenticated write routes
# ---------------------------------------------------------------------------


def test_toggle_disables_critic(services):
    ac = TestClient(create_app(services, token=_TOKEN), follow_redirects=False)
    assert services.config.is_enabled("critic.duplication") is True
    csrf = _csrf(ac)
    resp = ac.post(
        "/toggle",
        data={"flag": "critic.duplication", "value": "false", "csrf_token": csrf},
        headers={"Authorization": f"Bearer {_TOKEN}"},
    )
    assert resp.status_code == 303
    assert resp.headers["location"] == "/"
    assert services.config.is_enabled("critic.duplication") is False


def test_set_value_updates_threshold(services):
    ac = TestClient(create_app(services, token=_TOKEN), follow_redirects=False)
    csrf = _csrf(ac)
    resp = ac.post(
        "/set-value",
        data={"key": "default_threshold", "value": "0.8", "csrf_token": csrf},
        headers={"Authorization": f"Bearer {_TOKEN}"},
    )
    assert resp.status_code == 303
    assert services.config.get_value("default_threshold") == pytest.approx(0.8)


# ---------------------------------------------------------------------------
# CSRF protection
# ---------------------------------------------------------------------------


def test_missing_csrf_token_returns_403(services):
    """Authenticated POST without CSRF token must be rejected with 403."""
    ac = TestClient(create_app(services, token=_TOKEN), follow_redirects=False)
    # GET first so the CSRF cookie is set, but do NOT include the csrf_token field.
    ac.get("/")
    resp = ac.post(
        "/toggle",
        data={"flag": "dry_run", "value": "false"},
        headers={"Authorization": f"Bearer {_TOKEN}"},
    )
    assert resp.status_code == 403


def test_wrong_csrf_token_returns_403(services):
    """Authenticated POST with a mismatched CSRF token must be rejected with 403."""
    ac = TestClient(create_app(services, token=_TOKEN), follow_redirects=False)
    ac.get("/")
    resp = ac.post(
        "/toggle",
        data={"flag": "dry_run", "value": "false", "csrf_token": "bad-csrf"},
        headers={"Authorization": f"Bearer {_TOKEN}"},
    )
    assert resp.status_code == 403


# ---------------------------------------------------------------------------
# Open (local) mode -- existing behaviour preserved
# ---------------------------------------------------------------------------


def test_toggle_open_mode_no_auth_needed(services):
    """In local/open mode, writes succeed without any auth or CSRF token."""
    c = TestClient(create_app(services), follow_redirects=False)
    resp = c.post("/toggle", data={"flag": "critic.duplication", "value": "false"})
    assert resp.status_code == 303
    assert services.config.is_enabled("critic.duplication") is False


def test_set_value_open_mode_no_auth_needed(services):
    c = TestClient(create_app(services), follow_redirects=False)
    resp = c.post("/set-value", data={"key": "default_threshold", "value": "0.8"})
    assert resp.status_code == 303
    assert services.config.get_value("default_threshold") == pytest.approx(0.8)