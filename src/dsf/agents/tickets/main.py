"""Tickets agent entrypoint — STUB (plan Task 2.6).

Builds the A2A app over the fake (empty) tickets backend. Run with
``uvicorn dsf.agents.tickets.main:app``. The real backend is deferred; see
:mod:`dsf.agents.tickets.backend`.
"""

from __future__ import annotations

from dsf.agents.base import SourceAgent
from dsf.agents.tickets.backend import TicketsFixtureBackend
from dsf.config.store import InMemoryConfigStore
from dsf.contracts.enums import SourceKind


def build_agent(config: object | None = None) -> SourceAgent:
    """Build the tickets :class:`SourceAgent` over the fake backend."""
    cfg = config if config is not None else InMemoryConfigStore.from_defaults()
    return SourceAgent(
        kind=SourceKind.TICKETS,
        backend=TicketsFixtureBackend(),
        config=cfg,  # type: ignore[arg-type]
        capabilities=["gather"],
    )


#: ASGI app served by uvicorn (auth read from ``A2A_BEARER_TOKEN`` env var).
app = build_agent().make_app()


__all__ = ["app", "build_agent"]
