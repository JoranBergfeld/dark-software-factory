"""A2A request/response envelopes wrapping the shared contracts."""

from __future__ import annotations

from pydantic import BaseModel, Field

from dsf.contracts.models import EvidenceItem


class A2ARequest(BaseModel):
    """Request envelope for ``POST /gather``.

    ``run_scope`` is a serialized subset of a :class:`~dsf.contracts.models.Run`
    (product hints, source kinds, signal payload) — a plain dict so agents stay
    decoupled from the full blackboard object.
    """

    run_scope: dict = Field(default_factory=dict)


class A2AResponse(BaseModel):
    """Response envelope for ``POST /gather``.

    ``degraded`` is set (with ``error`` populated) whenever the agent could not
    fully gather evidence — e.g. it is disabled, its backend raised, or the
    client hit a transport/timeout error. Coverage is never fabricated.
    """

    evidence: list[EvidenceItem] = Field(default_factory=list)
    degraded: bool = False
    error: str | None = None
