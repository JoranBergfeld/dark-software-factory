"""Shared Agent-to-Agent (A2A) library (plan Task 2.0).

Each source agent is a thin FastAPI service exposing an Agent Card and a
``/gather`` endpoint over a shared envelope contract. The orchestrator talks to
agents through :mod:`dsf.a2a.client`, which can target either a real HTTP
endpoint or an in-process ASGI app (used during local dry-run).
"""

from dsf.a2a.auth import build_bearer_dependency, require_bearer
from dsf.a2a.card import AgentCard
from dsf.a2a.client import fetch_card, gather
from dsf.a2a.envelope import A2ARequest, A2AResponse
from dsf.a2a.server import make_agent_app

__all__ = [
    "A2ARequest",
    "A2AResponse",
    "AgentCard",
    "build_bearer_dependency",
    "fetch_card",
    "gather",
    "make_agent_app",
    "require_bearer",
]
