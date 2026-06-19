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

_FIXTURE = Path(__file__).resolve().parents[3] / "tests" / "fixtures" / "sample_signal.json"


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
    # Nothing ran and nothing was queued.
    assert services.github.calls == []
    import asyncio

    assert asyncio.run(services.signals.drain()) == []


def test_ingest_enqueues_signal_and_does_not_run_the_line(
    client: TestClient, services: Services
) -> None:
    import asyncio

    payload = json.loads(_FIXTURE.read_text(encoding="utf-8"))

    resp = client.post("/ingest", json=payload)

    assert resp.status_code == 200
    assert resp.json() == {"status": "queued"}
    # The line did not run synchronously: nothing was filed, and the signal is
    # waiting in the buffer for the scheduled drain (governed pull, ADR 0011).
    assert services.github.calls == []
    assert asyncio.run(services.signals.drain()) == [payload]


def test_ingest_duplicate_signal_suppressed(client: TestClient, services: Services) -> None:
    import asyncio

    payload = json.loads(_FIXTURE.read_text(encoding="utf-8"))

    first = client.post("/ingest", json=payload)
    assert first.json() == {"status": "queued"}

    second = client.post("/ingest", json=payload)
    assert second.json() == {"status": "suppressed"}

    # Only the first, accepted signal is buffered; the suppressed repeat is not.
    assert asyncio.run(services.signals.drain()) == [payload]


def test_app_importable() -> None:
    assert app is not None


def test_file_endpoint_runs_without_dry_run(client: TestClient, services: Services) -> None:
    """/file runs the pipeline with dry_run=False (S7 still dry-runs due to config flag)."""
    payload = json.loads(_FIXTURE.read_text(encoding="utf-8"))
    resp = client.post("/file", json=payload)
    assert resp.status_code == 200
    body = resp.json()
    assert body["run_id"]
    assert body["status"] in {
        RunStatus.FILED.value,
        RunStatus.KILLED.value,
        RunStatus.ERROR.value,
    }


def test_file_endpoint_paused_returns_paused(client: TestClient, services: Services) -> None:
    services.config.set_flag("trigger.SIGNAL.paused", True)
    resp = client.post("/file", json={"text": "boom"})
    assert resp.status_code == 200
    assert resp.json() == {"status": "paused"}


def test_file_endpoint_does_not_debounce(client: TestClient, services: Services) -> None:
    """/file can be called multiple times for the same payload without suppression."""
    payload = {"text": "deliberate filing request", "product_hints": ["alpha"]}
    r1 = client.post("/file", json=payload)
    r2 = client.post("/file", json=payload)
    # Neither should return "suppressed"
    assert r1.json().get("status") != "suppressed"
    assert r2.json().get("status") != "suppressed"
