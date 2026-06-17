"""Base source agent (plan Task 2.1).

A :class:`SourceAgent` binds a :class:`~dsf.contracts.enums.SourceKind` to a
:class:`~dsf.ports.SourceBackend` and a :class:`~dsf.ports.ConfigStore`. It
honors the agent feature flag and wraps backend calls so a disabled agent or a
backend failure degrades gracefully instead of fabricating coverage.
"""

from __future__ import annotations

import logging
from typing import TYPE_CHECKING

from dsf.a2a.card import AgentCard
from dsf.a2a.envelope import A2AResponse
from dsf.a2a.server import make_agent_app
from dsf.config.flags import agent_enabled

if TYPE_CHECKING:
    from fastapi import FastAPI

    from dsf.contracts.enums import SourceKind
    from dsf.ports import ConfigStore, SourceBackend

_logger = logging.getLogger(__name__)


class SourceAgent:
    """An A2A source agent over a pluggable backend."""

    def __init__(
        self,
        kind: SourceKind,
        backend: SourceBackend,
        config: ConfigStore,
        *,
        name: str | None = None,
        endpoint: str = "",
        capabilities: list[str] | None = None,
    ) -> None:
        self.kind = kind
        self.backend = backend
        self.config = config
        self.name = name or f"{kind.value.lower()}-agent"
        self.endpoint = endpoint
        self.capabilities = list(capabilities or ["gather"])

    def card(self) -> AgentCard:
        """Describe this agent, reflecting its current enabled flag."""
        return AgentCard(
            name=self.name,
            kind=self.kind,
            endpoint=self.endpoint,
            capabilities=list(self.capabilities),
            enabled=agent_enabled(self.config, self.kind),
        )

    async def gather(self, scope: dict) -> A2AResponse:
        """Gather evidence for ``scope``, degrading on disable/failure.

        * Disabled via ``agent.<KIND>`` flag -> degraded empty response.
        * Backend raises -> degraded empty response carrying the error string.
        * Otherwise -> the backend's evidence wrapped in a healthy response.
        """
        if not agent_enabled(self.config, self.kind):
            return A2AResponse(evidence=[], degraded=True, error="agent disabled")
        try:
            evidence = await self.backend.gather(dict(scope))
        except Exception:  # noqa: BLE001 - degrade, never propagate
            _logger.error("source agent %s gather failed", self.kind.value, exc_info=True)
            return A2AResponse(
                evidence=[], degraded=True, error=f"source {self.kind.value} gather failed"
            )
        return A2AResponse(evidence=list(evidence), degraded=False, error=None)

    def make_app(self, token: str | None = None) -> FastAPI:
        """Build the FastAPI app serving this agent over A2A."""
        return make_agent_app(self, token=token)


__all__ = ["SourceAgent"]
