"""Product Charter contracts — the human-owned statement of product intent.

A :class:`Charter` is the parsed `.dsf/charter.md`. A :class:`StoredCharter` is
the singleton-per-product record persisted to Cosmos with its sync status.
"""

from __future__ import annotations

from datetime import datetime

from pydantic import BaseModel, Field

from dsf.contracts.enums import CharterStatus


class Charter(BaseModel):
    """A product's charter: vision, users, goals, non-goals, metrics."""

    product: str
    schema_version: int = 1
    source_sha: str | None = None
    source_ref: str | None = None
    vision: str
    target_users: str
    goals: list[str] = Field(default_factory=list)
    non_goals: list[str] = Field(default_factory=list)
    success_metrics: list[str] = Field(default_factory=list)
    constraints: str = ""
    glossary: dict[str, str] = Field(default_factory=dict)


class StoredCharter(BaseModel):
    """A charter plus its Cosmos sync metadata (one per product)."""

    product: str
    charter: Charter | None = None
    status: CharterStatus
    last_synced_at: datetime | None = None
    last_error: str | None = None


__all__ = ["Charter", "StoredCharter"]
