"""Instance spec + manifest models and on-disk IO.

An :class:`InstanceSpec` is the *desired state* for one isolated product
factory. A :class:`ProvisionStep` is a single ordered action; an
:class:`InstancePlan` is the full ordered sequence; an
:class:`InstanceManifest` is the persisted record (spec + plan + status).
"""

from __future__ import annotations

import re
from pathlib import Path

from pydantic import BaseModel, Field, field_validator

_NAME_PREFIX_RE = re.compile(r"^[a-z][a-z0-9]{2,11}$")


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
    runtime_target: str = "aca"
    runtime_image: str = "ghcr.io/joranbergfeld/dsf-runtime:latest"
    confidence_threshold: float = 0.6
    name_prefix: str = "dsf"
    creation_maturity: str = "low"
    environment: str = "dev"
    location: str = "swedencentral"
    label_taxonomy: dict[str, list[str]] = Field(default_factory=default_label_taxonomy)
    sre_agent_location: str = "swedencentral"
    monitored_resource_groups: list[str] = Field(default_factory=list)

    @field_validator("sre_agent_location")
    @classmethod
    def _validate_sre_agent_location(cls, value: str) -> str:
        # Regions where Microsoft.App/agents is offered (live provider list, 2026-06):
        # az provider show --namespace Microsoft.App
        #   --query "resourceTypes[?resourceType=='agents'].locations"
        supported = {
            "swedencentral",
            "uksouth",
            "eastus2",
            "australiaeast",
            "francecentral",
            "canadacentral",
            "koreacentral",
        }
        if value not in supported:
            raise ValueError(
                f"sre_agent_location must be one of {sorted(supported)}, got {value!r}"
            )
        return value

    @field_validator("name_prefix")
    @classmethod
    def _validate_name_prefix(cls, value: str) -> str:
        if not _NAME_PREFIX_RE.match(value):
            raise ValueError(
                f"name_prefix must be 3-12 chars, lowercase alnum, start with a letter: {value!r}"
            )
        return value

    @field_validator("creation_maturity")
    @classmethod
    def _validate_creation_maturity(cls, value: str) -> str:
        if value not in {"low", "high"}:
            raise ValueError(f"creation_maturity must be 'low' or 'high', got {value!r}")
        return value

    def resolved_repo(self) -> str:
        """Repository name (defaults to the product key)."""
        return self.repo or self.product

    def github_repo(self) -> str:
        """``owner/repo`` slug for the product repository."""
        return f"{self.owner}/{self.resolved_repo()}"

    def resource_group(self) -> str:
        """Dedicated Azure resource-group name for this instance."""
        return f"rg-dsf-{self.product}"

    def deployment_name(self) -> str:
        """ARM deployment name for this instance's Azure provisioning."""
        return f"dsf-{self.product}"

    def sre_agent_name(self) -> str:
        """Azure SRE Agent resource name for this instance."""
        return f"dsf-sre-{self.product}"

    def sre_resource_group(self) -> str:
        """Dedicated resource group that hosts the SRE agent."""
        return f"rg-dsf-sre-{self.product}"

    def monitored_rgs(self) -> list[str]:
        """Resource groups the agent monitors: the factory RG plus any extras, deduped."""
        ordered = [self.resource_group(), *self.monitored_resource_groups]
        seen: set[str] = set()
        out: list[str] = []
        for rg in ordered:
            if rg not in seen:
                seen.add(rg)
                out.append(rg)
        return out


class ProvisionStep(BaseModel):
    """A single ordered provisioning action."""

    name: str
    description: str
    command: list[str] = Field(default_factory=list)
    commands: list[list[str]] = Field(default_factory=list)
    cwd: str = ""
    deferred: bool = False
    executed: bool = False
    result: str = ""
    error: str = ""


class InstancePlan(BaseModel):
    """Ordered provisioning steps for one instance."""

    product: str
    steps: list[ProvisionStep]


class TeardownPlan(BaseModel):
    """Ordered teardown steps for one instance (inverse of :class:`InstancePlan`)."""

    product: str
    steps: list[ProvisionStep]


class AzureProvisionResult(BaseModel):
    """Captured outcome of the Azure deployment for one instance."""

    resource_group: str
    deployment_name: str
    location: str
    outputs: dict[str, str] = Field(default_factory=dict)


class GitHubAppBinding(BaseModel):
    """The DSF GitHub App binding captured for one product at provision time.

    ``app_id`` and ``installation_id`` are owner-level (shared across products);
    ``repository_id`` is this product's repo, added to that single installation.
    ``private_key_secret`` is the product Key Vault secret name the runtime reads.
    """

    app_id: str
    installation_id: str
    repository_id: int
    private_key_secret: str = "github-app-private-key"


class InstanceManifest(BaseModel):
    """Persisted record of an instance: spec + plan + execution status."""

    spec: InstanceSpec
    plan: InstancePlan
    executed: bool = False
    azure: AzureProvisionResult | None = None
    github_app: GitHubAppBinding | None = None


def _default_repo_root() -> Path:
    """Repo root (where ``config/`` lives): four parents up from this file."""
    return Path(__file__).resolve().parents[4]


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
