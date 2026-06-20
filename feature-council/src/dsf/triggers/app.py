"""Trigger HTTP app — liveness only.

DSF is pull-only: the orchestrator gets work by sweeping source agents on its own
schedule (see :func:`dsf.triggers.scheduler.sweep`). There is no inbound signal
inbox, so this app exposes only a liveness probe.

The app builds a module-level local :class:`~dsf.container.Services` at import
time. Tests can override it via FastAPI's dependency system
(``app.dependency_overrides[get_services] = ...``) so each test gets a fresh,
isolated services bundle - keeping the app testable without global state.
"""

from __future__ import annotations

from typing import TYPE_CHECKING

from fastapi import FastAPI

from dsf.container import build_services

if TYPE_CHECKING:
    from dsf.container import Services

#: Module-level local services bundle, shared across requests by default.
_LOCAL_SERVICES: Services = build_services("local")


def get_services() -> Services:
    """Dependency provider for the request-scoped services bundle.

    Returns the module-level local bundle by default; override in tests via
    ``app.dependency_overrides[get_services]``.
    """
    return _LOCAL_SERVICES


app = FastAPI(title="dsf-triggers", version="1.0.0")


@app.get("/health")
async def health() -> dict[str, str]:
    """Liveness probe."""
    return {"status": "ok"}


__all__ = ["app", "get_services"]
