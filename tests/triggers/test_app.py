"""Tests for the ingestion HTTP endpoint (plan Task 5.2)."""

from __future__ import annotations

import json
from collections.abc import Iterator
from pathlib import Path

import pytest
from fastapi.testclient import TestClient

from dsf.container import Services, build_services
from dsf.contracts.enums import RunStatus
from dsf.triggers.app import app, get_services

_FIXTURE = Path(__file__).resolve().parents[1] / "fixtures" / "sample_signal.json"


@pytest.fixture
def services() -> Services:
    """A fresh, isolated local services bundle for each test."""
    return build_services("local")


@pytest.fixture
def client(services: Services) -> Iterator[TestClient]:
    """TestClient wired to the per-test services bundle via dependency override."""
    app.dependency_overrides[get_services] = lambda: services
    try:
        yield TestClient(app)
    finally:
        app.dependency_overrides.clear()


def test_ingest_paused_signal_returns_paused(client: TestClient, services: Services) -> None:
    services.config.set_flag("trigger.SIGNAL.paused", True)

    resp = client.post("/ingest", json={"text": "error spike", "product_hints": ["microbi"]})

    assert resp.status_code == 200
    assert resp.json() == {"status": "paused"}
    # Nothing ran.
    assert services.github.calls == []


def test_ingest_sample_signal_runs_dry_run_line(client: TestClient, services: Services) -> None:
    payload = json.loads(_FIXTURE.read_text(encoding="utf-8"))

    resp = client.post("/ingest", json=payload)

    assert resp.status_code == 200
    body = resp.json()
    # A run id is returned and the run reached a known terminal status.
    assert body["run_id"]
    assert body["status"] in {
        RunStatus.FILED.value,
        RunStatus.KILLED.value,
        RunStatus.ERROR.value,
    }
    # Dry-run: the GitHub fake was never called for a real issue.
    assert services.github.calls == []


def test_ingest_duplicate_signal_suppressed(client: TestClient, services: Services) -> None:
    payload = json.loads(_FIXTURE.read_text(encoding="utf-8"))

    first = client.post("/ingest", json=payload)
    assert first.json()["status"] != "suppressed"

    second = client.post("/ingest", json=payload)
    assert second.json() == {"status": "suppressed"}


def test_app_importable() -> None:
    assert app is not None
