"""Product contract — config-as-data describing one product the line serves.

A :class:`Product` carries everything the conveyor needs to scope a run to a
product (S2) and route a surviving proposal to a repo + label taxonomy (S6).
The record now lives in the product's App Configuration; see
:func:`dsf.config.flags.product_record`.
"""

from __future__ import annotations

from pydantic import BaseModel, Field


class Product(BaseModel):
    """A single product the intake line serves."""

    key: str
    github_repo: str
    label_taxonomy: dict[str, list[str]] = Field(default_factory=dict)
    foundryiq_scope: str = ""
    sentry_projects: list[str] = Field(default_factory=list)
    grafana_dashboards: list[str] = Field(default_factory=list)
    azure_monitor_scope: str = ""
    confidence_threshold: float = 0.6


__all__ = ["Product"]
