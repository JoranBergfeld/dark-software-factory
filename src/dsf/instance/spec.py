"""Instance spec + manifest models and on-disk IO.

An :class:`InstanceSpec` is the *desired state* for one isolated product
factory. A :class:`ProvisionStep` is a single ordered action; an
:class:`InstancePlan` is the full ordered sequence; an
:class:`InstanceManifest` is the persisted record (spec + plan + status).
"""

from __future__ import annotations

from pathlib import Path

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


class ProvisionStep(BaseModel):
    """A single ordered provisioning action."""

    name: str
    description: str
    command: list[str] = Field(default_factory=list)
    cwd: str = ""
    deferred: bool = False
    executed: bool = False
    result: str = ""


class InstancePlan(BaseModel):
    """Ordered provisioning steps for one instance."""

    product: str
    steps: list[ProvisionStep]


class InstanceManifest(BaseModel):
    """Persisted record of an instance: spec + plan + execution status."""

    spec: InstanceSpec
    plan: InstancePlan
    executed: bool = False


def _default_repo_root() -> Path:
    """Repo root (where ``config/`` lives): three parents up from this file."""
    return Path(__file__).resolve().parents[3]


def instances_dir(repo_root: Path | None = None) -> Path:
    """Directory holding per-instance manifests (``config/instances/``)."""
    root = repo_root if repo_root is not None else _default_repo_root()
    return root / "config" / "instances"


def manifest_path(product: str, repo_root: Path | None = None) -> Path:
    """Path to a product's instance manifest."""
    return instances_dir(repo_root) / f"{product}.json"


def write_manifest(manifest: InstanceManifest, repo_root: Path | None = None) -> Path:
    """Write a manifest to ``config/instances/<product>.json`` and return the path."""
    path = manifest_path(manifest.spec.product, repo_root)
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(manifest.model_dump_json(indent=2), encoding="utf-8")
    return path


def read_manifest(product: str, repo_root: Path | None = None) -> InstanceManifest:
    """Read a product's instance manifest."""
    path = manifest_path(product, repo_root)
    return InstanceManifest.model_validate_json(path.read_text(encoding="utf-8"))
