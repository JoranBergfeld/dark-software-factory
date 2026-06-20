"""Incidents agent entrypoint.

Builds the A2A app over the incidents backend. Run with
``uvicorn dsf.agents.incidents.main:app``. The GitHub backend is selected only in
azure mode; see :mod:`dsf.agents.incidents.backend`.
"""

from __future__ import annotations

from dsf.agents.base import SourceAgent
from dsf.agents.incidents.backend import (
    IncidentsFixtureBackend,
    IncidentsGitHubBackend,
)
from dsf.agents.mode import is_live, resolve_mode
from dsf.config.store import InMemoryConfigStore
from dsf.contracts.enums import SourceKind


def build_agent(config: object | None = None, mode: str | None = None) -> SourceAgent:
    """Build the incidents :class:`SourceAgent`, selecting the backend by mode."""
    cfg = config if config is not None else InMemoryConfigStore.from_defaults()
    if is_live(resolve_mode(mode)):
        from dsf.agents.incidents.client import build_incidents_client_from_env

        backend = IncidentsGitHubBackend(gh_call=build_incidents_client_from_env())
    else:
        backend = IncidentsFixtureBackend()
    return SourceAgent(
        kind=SourceKind.INCIDENTS,
        backend=backend,
        config=cfg,  # type: ignore[arg-type]
        capabilities=["gather"],
    )


#: ASGI app served by uvicorn (auth read from ``A2A_BEARER_TOKEN`` env var).
app = build_agent().make_app()


__all__ = ["app", "build_agent"]
