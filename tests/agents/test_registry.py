"""Tests for the deployable-agent registry (single source of truth)."""

from __future__ import annotations

import importlib

from dsf.agents.registry import DEPLOYABLE_AGENTS, app_path, serveable_agents
from dsf.contracts.enums import SourceKind


def test_registry_keys_match_source_kinds():
    # A new source agent must be registered (or explicitly excluded) — no drift.
    assert set(DEPLOYABLE_AGENTS) == {k.value.lower() for k in SourceKind}


def test_serveable_agents_is_sorted():
    assert serveable_agents() == sorted(DEPLOYABLE_AGENTS)


def test_app_path_returns_import_path():
    assert app_path("sentry") == "dsf.agents.sentry.main:app"


def test_app_path_unknown_returns_none():
    assert app_path("nope") is None


def test_every_registered_app_is_importable():
    for name, path in DEPLOYABLE_AGENTS.items():
        module_name, _, attr = path.partition(":")
        module = importlib.import_module(module_name)
        app = getattr(module, attr)
        # FastAPI/Starlette ASGI apps expose a routes collection and are callable.
        assert app is not None, name
        assert hasattr(app, "routes"), name
