"""FastAPI scaffold for a source agent service.

``make_agent_app(agent)`` wires the two A2A endpoints around any object that
implements :class:`AgentProtocol` (typically a
:class:`dsf.agents.base.SourceAgent`).
"""

from __future__ import annotations

from typing import Protocol

from fastapi import Depends, FastAPI

from dsf.a2a.auth import require_bearer
from dsf.a2a.card import AgentCard
from dsf.a2a.envelope import A2ARequest, A2AResponse


class AgentProtocol(Protocol):
    """The minimal surface :func:`make_agent_app` needs from an agent."""

    def card(self) -> AgentCard:
        """Return this agent's :class:`AgentCard`."""
        ...

    async def gather(self, scope: dict) -> A2AResponse:
        """Gather evidence for a serialized run scope."""
        ...


def make_agent_app(agent: AgentProtocol, token: str | None = None) -> FastAPI:
    """Build a FastAPI app exposing ``GET /card`` and ``POST /gather``.

    ``token`` configures bearer enforcement (see :func:`dsf.a2a.auth.require_bearer`):
    pass ``""`` to disable, a real token to enforce, or ``None`` to read the
    ``A2A_BEARER_TOKEN`` env var.
    """
    app = FastAPI(title=f"dsf-agent:{agent.card().kind.value}")
    auth_dep = require_bearer(token)

    @app.get("/card", response_model=AgentCard, dependencies=[Depends(auth_dep)])
    def get_card() -> AgentCard:
        return agent.card()

    @app.post("/gather", response_model=A2AResponse, dependencies=[Depends(auth_dep)])
    async def post_gather(request: A2ARequest) -> A2AResponse:
        return await agent.gather(request.run_scope)

    return app


__all__ = ["AgentProtocol", "make_agent_app"]
