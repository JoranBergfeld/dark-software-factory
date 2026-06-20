"""Tests for the trigger HTTP app (liveness only — DSF is pull-only)."""

from __future__ import annotations

from collections.abc import Iterator

import pytest
from fastapi.testclient import TestClient

from dsf.container import Services
from dsf.triggers.app import app, get_services
from dsf_testing import build_test_services


@pytest.fixture
def services() -> Services:
    """A fresh, isolated local services bundle for each test."""
    return build_test_services()


@pytest.fixture
def client(services: Services) -> Iterator[TestClient]:
    """TestClient wired to the per-test services bundle via dependency override."""
    app.dependency_overrides[get_services] = lambda: services
    try:
        yield TestClient(app)
    finally:
        app.dependency_overrides.clear()


def test_app_importable() -> None:
    assert app is not None


def test_health_returns_ok(client: TestClient) -> None:
    resp = client.get("/health")
    assert resp.status_code == 200
    assert resp.json() == {"status": "ok"}
