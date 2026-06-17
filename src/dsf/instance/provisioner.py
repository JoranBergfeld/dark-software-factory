"""InstanceProvisioner — build and apply a provisioning plan for an instance.

External CLIs (``gh``, ``squad``, ``az``) are invoked through an injectable
``run`` callable (defaults to :func:`subprocess.run`) so tests stay offline,
mirroring :class:`dsf.github_client.RealGitHubClient`.
"""

from __future__ import annotations

import subprocess
from collections.abc import Callable
from pathlib import Path
from typing import Any

from dsf.instance.spec import (
    InstanceManifest,
    InstancePlan,
    InstanceSpec,
    ProvisionStep,
    manifest_path,
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
                name="provision_azure",
                description=(
                    f"Provision dedicated Azure resource group {s.resource_group()} "
                    "(deferred to SP2)"
                ),
                command=[
                    "az", "group", "create",
                    "--name", s.resource_group(),
                    "--location", "swedencentral",
                ],
                deferred=True,
            ),
            ProvisionStep(
                name="deploy_council",
                description=(
                    f"Deploy feature-council runtime scoped to {s.product} (deferred to SP3)"
                ),
                deferred=True,
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
        """
        plan = self.plan()
        for step in plan.steps:
            if step.name == "write_config":
                continue  # finalized after the manifest is built
            if step.deferred:
                step.result = "deferred"
            elif not execute:
                step.result = "dry-run"
            elif not step.command:
                step.result = "noop"
            elif step.name == "create_repo" and self._repo_exists():
                step.executed, step.result = True, "exists"
            else:
                kwargs = {"cwd": step.cwd} if step.cwd else {}
                self._run(step.command, check=True, **kwargs)
                step.executed, step.result = True, "executed"

        manifest = InstanceManifest(spec=self.spec, plan=plan, executed=execute)
        path = manifest_path(self.spec.product, self._repo_root)
        for step in plan.steps:
            if step.name == "write_config":
                step.executed, step.result = True, str(path)
        write_manifest(manifest, self._repo_root)
        return manifest

    def _repo_exists(self) -> bool:
        """Return True if the product repo already exists (``gh repo view``)."""
        result = self._run(
            ["gh", "repo", "view", self.spec.github_repo()],
            capture_output=True,
            text=True,
            check=False,
        )
        return getattr(result, "returncode", 1) == 0
