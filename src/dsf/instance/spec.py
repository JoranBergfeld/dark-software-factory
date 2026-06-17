"""Instance spec + manifest models and on-disk IO.

An :class:`InstanceSpec` is the *desired state* for one isolated product
factory. A :class:`ProvisionStep` is a single ordered action; an
:class:`InstancePlan` is the full ordered sequence; an
:class:`InstanceManifest` is the persisted record (spec + plan + status).
"""

from __future__ import annotations

from pydantic import BaseModel, Field


def default_label_taxonomy() -> dict[str, list[str]]:
    """Default GitHub label taxonomy applied to a new product."""
    return {
        "type": ["feature", "bug", "chore"],
        "area": ["api", "ui", "infra"],
        "severity": ["sev-low", "sev-medium", "sev-high", "sev-critical"],
    }


class InstanceSpec(BaseModel):
    """Desired state for one isolated product factory instance."""

    product: str
    owner: str
    repo: str = ""
    visibility: str = "private"
    runtime_target: str = "homelab"
    confidence_threshold: float = 0.6
    label_taxonomy: dict[str, list[str]] = Field(default_factory=default_label_taxonomy)

    def resolved_repo(self) -> str:
        """Repository name (defaults to the product key)."""
        return self.repo or self.product

    def github_repo(self) -> str:
        """``owner/repo`` slug for the product repository."""
        return f"{self.owner}/{self.resolved_repo()}"

    def resource_group(self) -> str:
        """Dedicated Azure resource-group name for this instance."""
        return f"rg-dsf-{self.product}"
