"""InstanceDeprovisioner — build and apply a teardown plan for a factory instance.

Inverse of :class:`~dsf.instance.provisioner.InstanceProvisioner`. Tears down
everything that ``dsf new`` created, in the reverse order: Azure resources and
registry entries first, the GitHub repo last (so a mid-run failure never orphans
Azure resources without their manifest to find them again).

External CLIs (``gh``, ``az``) are invoked through an injectable ``run`` callable
(defaults to :func:`subprocess.run`) so tests stay offline, mirroring
:class:`~dsf.instance.provisioner.InstanceProvisioner`.
"""

from __future__ import annotations

import re
import shutil
import subprocess
from collections.abc import Callable
from pathlib import Path
from typing import Any

from dsf.config.registry import deregister_product
from dsf.instance.runtime_render import runtime_dir
from dsf.instance.spec import (
    InstanceManifest,
    ProvisionStep,
    TeardownPlan,
    _default_repo_root,
    manifest_path,
)
from dsf.instance.teardown_common import (
    ALREADY_ABSENT_RESULT,
    guarded_group_delete,
    is_not_found,
)

Runner = Callable[..., Any]

#: Progress callback: ``(phase, index, total, step, error)`` where ``phase`` is
#: ``"start"`` / ``"done"`` / ``"error"``. ``error`` is set only on ``"error"``.
StepEvent = Callable[[str, int, int, ProvisionStep, "BaseException | None"], None]

#: Cap captured stderr/stdout folded into a step error so a runaway tool log
#: doesn't flood the summary.
_MAX_ERROR_DETAIL = 2000


def _format_step_error(exc: BaseException) -> str:
    """Build a readable error message for a failed step, folding in captured output."""
    parts = [str(exc)]
    for attr in ("stderr", "stdout"):
        detail = getattr(exc, attr, None)
        if isinstance(detail, bytes):
            detail = detail.decode(errors="replace")
        detail = (detail or "").strip() if isinstance(detail, str) else ""
        if detail:
            parts.append(detail[:_MAX_ERROR_DETAIL])
    return "\n".join(parts)


class InstanceDeprovisioner:
    """Builds the ordered teardown plan for a product instance and applies it.

    Parameters
    ----------
    manifest:
        The persisted manifest read from ``config/instances/<product>.json``.
        Contains the spec (owner, resource groups, etc.) and Azure outputs
        needed to identify every resource to tear down.
    run:
        Optional ``subprocess.run``-compatible callable. Inject a mock in tests.
    repo_root:
        Optional override for where ``config/instances/`` lives (tests/CI).
    purge:
        When ``True``, purge soft-deleted Key Vault resources after the resource
        group is deleted, freeing the name for immediate reuse.
    delete_repo:
        When ``True`` (default, used by ``dsf delete``), include a ``delete_repo``
        step that removes the GitHub repository. Set to ``False`` for Azure-only
        teardown (``dsf offboard``).
    """

    def __init__(
        self,
        manifest: InstanceManifest,
        *,
        run: Runner | None = None,
        repo_root: Path | None = None,
        purge: bool = False,
        delete_repo: bool = True,
    ) -> None:
        self.manifest = manifest
        self.spec = manifest.spec
        self._run = run or subprocess.run
        self._repo_root = repo_root
        self._purge = purge
        self._delete_repo = delete_repo

    # ------------------------------------------------------------------
    # Plan building
    # ------------------------------------------------------------------

    def plan(self) -> TeardownPlan:
        """Return the ordered teardown plan (pure — no side effects)."""
        s = self.spec
        steps: list[ProvisionStep] = [
            ProvisionStep(
                name="delete_sre_agent",
                description=(
                    f"Delete SRE agent resource group {s.sre_resource_group()} "
                    f"(agent: {s.sre_agent_name()})"
                ),
                command=[
                    "az", "group", "delete",
                    "--name", s.sre_resource_group(),
                    "--yes",
                ],
            ),
            ProvisionStep(
                name="delete_resource_group",
                description=f"Delete product resource group {s.resource_group()}",
                command=[
                    "az", "group", "delete",
                    "--name", s.resource_group(),
                    "--yes",
                ],
            ),
        ]

        if self._purge:
            vault_name = self._keyvault_name()
            if vault_name:
                steps.append(ProvisionStep(
                    name="purge_keyvault",
                    description=f"Purge soft-deleted Key Vault {vault_name}",
                    command=[
                        "az", "keyvault", "purge",
                        "--name", vault_name,
                        "--location", s.location,
                    ],
                ))

        steps += [
            ProvisionStep(
                name="deregister_product",
                description=(
                    f"Remove {s.product} from config/products.json routing registry"
                ),
            ),
            ProvisionStep(
                name="delete_config",
                description=(
                    f"Delete manifest config/instances/{s.product}.json "
                    f"and runtime bundle {s.product}.runtime/"
                ),
            ),
        ]

        if self._delete_repo:
            steps.append(ProvisionStep(
                name="delete_repo",
                description=f"Delete GitHub repo {s.github_repo()} (irreversible)",
                command=["gh", "repo", "delete", s.github_repo(), "--yes"],
            ))

        return TeardownPlan(product=s.product, steps=steps)

    # ------------------------------------------------------------------
    # Application
    # ------------------------------------------------------------------

    def apply(
        self,
        *,
        execute: bool = False,
        on_event: StepEvent | None = None,
    ) -> TeardownPlan:
        """Apply the teardown plan.

        ``execute=False`` is a side-effect-free dry-run that prints the plan
        without making any changes. ``execute=True`` runs each step.

        ``on_event`` is an optional progress callback invoked as
        ``on_event(phase, index, total, step, error)`` — ``phase`` is ``"start"``
        before a step runs, ``"done"`` after it succeeds, and ``"error"`` if it
        fails.

        A step that raises is recorded on that step (``result="failed"``,
        ``error=<message>``) and **stops the run** (later steps are left unrun).
        ``apply`` does not propagate the exception.

        Already-absent resources (404s) are tolerated — the step records
        ``"not-found (already absent)"`` and continues so a re-run can finish a
        partial delete.
        """
        emit = on_event or (lambda *_a: None)
        plan = self.plan()
        total = len(plan.steps)

        for index, step in enumerate(plan.steps, 1):
            emit("start", index, total, step, None)
            try:
                if not execute:
                    step.result = "dry-run"
                else:
                    self._execute_step(step)
            except Exception as exc:  # noqa: BLE001 - intentionally caught; converted to step failure, reported via on_event
                step.executed = False
                step.result = "failed"
                step.error = _format_step_error(exc)
                emit("error", index, total, step, exc)
                break
            emit("done", index, total, step, None)

        return plan

    # ------------------------------------------------------------------
    # Step execution
    # ------------------------------------------------------------------

    def _execute_step(self, step: ProvisionStep) -> None:
        """Run one teardown step, mutating ``step.result`` / ``step.executed``."""
        if step.name == "deregister_product":
            products_json = (
                (self._repo_root or _default_repo_root()) / "config" / "products.json"
            )
            deregister_product(self.spec.product, path=products_json)
            step.executed, step.result = True, "deregistered"

        elif step.name == "delete_config":
            self._delete_config_files()
            step.executed, step.result = True, "deleted"

        elif step.name in ("delete_sre_agent", "delete_resource_group"):
            name = (
                self.spec.sre_resource_group()
                if step.name == "delete_sre_agent"
                else self.spec.resource_group()
            )
            step.result = guarded_group_delete(name, self._run)
            step.executed = True

        elif step.command:
            try:
                self._run(step.command, check=True)
                step.executed, step.result = True, "executed"
            except subprocess.CalledProcessError as exc:
                if is_not_found(exc):
                    step.executed = True
                    step.result = ALREADY_ABSENT_RESULT
                else:
                    raise

        else:
            step.result = "noop"

    # ------------------------------------------------------------------
    # Helpers
    # ------------------------------------------------------------------

    def _keyvault_name(self) -> str:
        """Extract the Key Vault name from the persisted Azure deployment outputs.

        The ``keyVaultName`` output (preferred) is emitted directly by
        ``infra/main.bicep``. Falls back to parsing the ``keyVaultUri`` hostname.
        Returns an empty string when the manifest carries no Azure outputs.
        """
        if not self.manifest.azure:
            return ""
        outputs = self.manifest.azure.outputs
        name = outputs.get("keyVaultName", "")
        if name:
            return name
        # Fallback: parse "https://<name>.vault.azure.net/".
        uri = outputs.get("keyVaultUri", "")
        if not uri:
            return ""
        m = re.match(r"https://([^.]+)\.vault\.azure\.net", uri)
        return m.group(1) if m else ""

    def _delete_config_files(self) -> None:
        """Delete the per-product manifest and runtime bundle from ``config/``."""
        mpath = manifest_path(self.spec.product, self._repo_root)
        rdir = runtime_dir(self.spec.product, self._repo_root)
        if rdir.exists():
            shutil.rmtree(rdir)
        if mpath.exists():
            mpath.unlink()

    @classmethod
    def from_product(
        cls,
        product: str,
        *,
        run: Runner | None = None,
        repo_root: Path | None = None,
        purge: bool = False,
        delete_repo: bool = True,
    ) -> InstanceDeprovisioner:
        """Load the persisted manifest for ``product`` and return a deprovisioner.

        Raises :class:`FileNotFoundError` if the manifest does not exist (i.e. the
        product was never provisioned or the manifest was already removed).
        """
        from dsf.instance.spec import read_manifest

        manifest = read_manifest(product, repo_root)
        return cls(
            manifest,
            run=run,
            repo_root=repo_root,
            purge=purge,
            delete_repo=delete_repo,
        )
