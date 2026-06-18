"""InstanceProvisioner — build and apply a provisioning plan for an instance.

External CLIs (``gh``, ``squad``, ``az``) are invoked through an injectable
``run`` callable (defaults to :func:`subprocess.run`) so tests stay offline,
mirroring :class:`dsf.github_client.RealGitHubClient`.
"""

from __future__ import annotations

import json
import subprocess
from collections.abc import Callable
from pathlib import Path
from typing import Any

from dsf.instance.runtime_render import render_runtime_bundle
from dsf.instance.spec import (
    AzureProvisionResult,
    InstanceManifest,
    InstancePlan,
    InstanceSpec,
    ProvisionStep,
    _default_repo_root,
    manifest_path,
    read_manifest,
    write_manifest,
)

Runner = Callable[..., Any]


class InstanceProvisioner:
    """Builds the ordered plan for an instance and applies it.

    Parameters
    ----------
    spec:
        Desired instance state.
    run:
        Optional ``subprocess.run``-compatible callable. Inject a mock in tests.
    repo_root:
        Optional override for where ``config/instances/`` lives (tests/CI).
    """

    def __init__(
        self,
        spec: InstanceSpec,
        *,
        run: Runner | None = None,
        repo_root: Path | None = None,
    ) -> None:
        self.spec = spec
        self._run = run or subprocess.run
        self._repo_root = repo_root

    def plan(self) -> InstancePlan:
        """Return the ordered provisioning plan (pure — no side effects)."""
        s = self.spec
        repo_dir = s.resolved_repo()
        bicep = str((self._repo_root or _default_repo_root()) / "infra" / "main.bicep")
        steps = [
            ProvisionStep(
                name="create_repo",
                description=f"Create GitHub repo {s.github_repo()} ({s.visibility})",
                command=[
                    "gh", "repo", "create", s.github_repo(),
                    f"--{s.visibility}", "--clone",
                ],
            ),
            ProvisionStep(
                name="squad_init",
                description=f"Initialize Coding Squad in {s.github_repo()}",
                command=["squad", "init", "--preset", "default"],
                cwd=repo_dir,
            ),
            ProvisionStep(
                name="squad_copilot",
                description="Enable Copilot coding agent auto-assignment",
                command=["squad", "copilot", "--auto-assign"],
                cwd=repo_dir,
            ),
            ProvisionStep(
                name="create_resource_group",
                description=f"Create dedicated Azure resource group {s.resource_group()}",
                command=[
                    "az", "group", "create",
                    "--name", s.resource_group(),
                    "--location", s.location,
                ],
            ),
            ProvisionStep(
                name="provision_azure",
                description=(
                    f"Deploy backing services into {s.resource_group()} from infra/main.bicep"
                ),
                command=[
                    "az", "deployment", "group", "create",
                    "-g", s.resource_group(),
                    "-n", s.deployment_name(),
                    "-f", bicep,
                    "-p",
                    f"namePrefix={s.name_prefix}",
                    f"environmentName={s.environment}",
                    f"location={s.location}",
                    f"product={s.product}",
                    f"runtimeImage={s.runtime_image}",
                    "--query", "properties.outputs", "-o", "json",
                ],
            ),
            ProvisionStep(
                name="deploy_council",
                description=(
                    f"Render + bring up the feature-council runtime scoped to {s.product}"
                ),
            ),
            ProvisionStep(
                name="deploy_sre",
                description=f"Deploy SRE agent for {s.product} (deferred to SP5)",
                deferred=True,
            ),
            ProvisionStep(
                name="write_config",
                description=f"Write instance manifest to config/instances/{s.product}.json",
            ),
        ]
        return InstancePlan(product=s.product, steps=steps)

    def apply(self, *, execute: bool = False) -> InstanceManifest:
        """Apply the plan. ``execute=False`` is a side-effect-free dry-run that
        still writes the manifest; ``execute=True`` runs the non-deferred,
        commanded steps via the injected runner.

        The manifest is always persisted — even if a step raises — so the
        randomized ``name_prefix`` survives a failed Azure deployment and a retry
        reuses the same resource names instead of orphaning the first attempt.
        Non-executing runs carry prior provisioning state forward so a preview or
        ``--write-plan`` never blanks recorded Azure outputs.
        """
        plan = self.plan()
        path = manifest_path(self.spec.product, self._repo_root)
        prior = self._existing_manifest()
        azure_result = prior.azure if (prior and not execute) else None
        executed = execute or bool(prior and prior.executed)
        try:
            for step in plan.steps:
                if step.name == "write_config":
                    continue  # finalized after the manifest is built
                if step.deferred:
                    step.result = "deferred"
                elif step.name == "deploy_council":
                    provisional = InstanceManifest(
                        spec=self.spec, plan=plan, executed=executed, azure=azure_result
                    )
                    render_runtime_bundle(provisional, repo_root=self._repo_root)
                    if not execute:
                        step.result = "rendered (dry-run)"
                    else:
                        self._run(
                            [
                                "az", "containerapp", "update",
                                "--resource-group", self.spec.resource_group(),
                                "--name", f"dsf-orchestrator-{self.spec.product}",
                                "--image", self.spec.runtime_image,
                            ],
                            check=True,
                        )
                        step.executed, step.result = True, "deployed"
                elif not execute:
                    step.result = "dry-run"
                elif not step.command:
                    step.result = "noop"
                elif step.name == "create_repo" and self._repo_exists():
                    step.executed = True
                    repo_dir = self.spec.resolved_repo()
                    if Path(repo_dir).is_dir():
                        step.result = "exists"
                    else:
                        # Repo exists remotely but isn't cloned here; the squad
                        # steps need a local working copy, so clone it.
                        self._run(
                            ["gh", "repo", "clone", self.spec.github_repo(), repo_dir],
                            check=True,
                        )
                        step.result = "cloned"
                elif step.name == "provision_azure":
                    proc = self._run(
                        step.command, check=True, capture_output=True, text=True
                    )
                    azure_result = self._azure_result(proc)
                    step.executed, step.result = True, "executed"
                else:
                    kwargs = {"cwd": step.cwd} if step.cwd else {}
                    self._run(step.command, check=True, **kwargs)
                    step.executed, step.result = True, "executed"
        finally:
            for step in plan.steps:
                if step.name == "write_config":
                    step.executed, step.result = True, str(path)
            manifest = InstanceManifest(
                spec=self.spec, plan=plan, executed=executed, azure=azure_result
            )
            write_manifest(manifest, self._repo_root)
        return manifest

    def _existing_manifest(self) -> InstanceManifest | None:
        """Return the persisted manifest for this product, or ``None`` if absent."""
        if not manifest_path(self.spec.product, self._repo_root).exists():
            return None
        try:
            return read_manifest(self.spec.product, self._repo_root)
        except (OSError, ValueError):
            return None

    def _repo_exists(self) -> bool:
        """Return True if the product repo already exists (``gh repo view``)."""
        result = self._run(
            ["gh", "repo", "view", self.spec.github_repo()],
            capture_output=True,
            text=True,
            check=False,
        )
        return getattr(result, "returncode", 1) == 0

    def _azure_result(self, proc: Any) -> AzureProvisionResult:
        """Parse ``az deployment group create --query properties.outputs`` JSON."""
        raw = getattr(proc, "stdout", None)
        parsed = json.loads(raw) if isinstance(raw, str) and raw.strip() else {}
        if not isinstance(parsed, dict):
            parsed = {}
        outputs = {
            k: (v.get("value") if isinstance(v, dict) else v) for k, v in parsed.items()
        }
        return AzureProvisionResult(
            resource_group=self.spec.resource_group(),
            deployment_name=self.spec.deployment_name(),
            location=self.spec.location,
            outputs={k: str(val) for k, val in outputs.items() if val is not None},
        )
