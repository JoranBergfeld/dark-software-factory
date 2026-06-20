"""Trigger HTTP app — liveness only.

DSF is pull-only: the orchestrator gets work by sweeping source agents on its own
schedule (see :func:`dsf.triggers.scheduler.sweep`). There is no inbound signal
inbox, so this app exposes only a liveness probe.

The app builds its real :class:`~dsf.container.Services` bundle lazily on first
request and caches it (so importing this module needs no Azure environment).
Tests override it via FastAPI's dependency system
(``app.dependency_overrides[get_services] = ...``) so each test gets a fresh,
isolated services bundle - keeping the app testable without global state.
"""

from __future__ import annotations

from typing import TYPE_CHECKING

from fastapi import FastAPI

from dsf.container import build_services

if TYPE_CHECKING:
    from dsf.container import Services

#: Lazily built real services bundle, shared across requests once constructed.
_services: Services | None = None


def get_services() -> Services:
    """Dependency provider for the request-scoped services bundle.

    Builds the real services bundle on first use and caches it; override in
    tests via ``app.dependency_overrides[get_services]``.
    """
    global _services
    if _services is None:
        _services = build_services()
    return _services


app = FastAPI(title="dsf-triggers", version="1.0.0")


@app.get("/health")
async def health() -> dict[str, str]:
    """Liveness probe."""
    return {"status": "ok"}


__all__ = ["app", "get_services"]
