"""Agent Card model — an agent's self-description served at ``GET /card``."""

from __future__ import annotations

from pydantic import BaseModel

from dsf.contracts.enums import SourceKind


class AgentCard(BaseModel):
    """Public description of a source agent.

    Mirrors the A2A "agent card" idea: a small, serializable manifest a caller
    can fetch to learn what an agent is and whether it is currently enabled.
    """

    name: str
    kind: SourceKind
    endpoint: str
    capabilities: list[str]
    enabled: bool
