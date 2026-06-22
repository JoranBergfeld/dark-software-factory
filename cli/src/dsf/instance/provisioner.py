"""InstanceProvisioner — build and apply a provisioning plan for an instance.

External CLIs (``gh``, ``squad``, ``az``) are invoked through an injectable
``run`` callable (defaults to :func:`subprocess.run`) so tests stay offline,
mirroring :class:`dsf.github_client.RealGitHubClient`.
"""

from __future__ import annotations

import json
import shutil
import subprocess
from collections.abc import Callable
from pathlib import Path
from typing import Any

from dsf.contracts.handoff import (
    HANDOFF_LABEL,
    HANDOFF_LABEL_COLOR,
    HANDOFF_LABEL_DESCRIPTION,
    INCIDENT_LABEL,
    INCIDENT_LABEL_COLOR,
    INCIDENT_LABEL_DESCRIPTION,
)
from dsf.instance.runtime_render import (
    render_product_registration,
    render_product_unregistration,
    render_runtime_bundle,
    render_sre_summary,
    runtime_dir,
)
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
from dsf.instance.squad_governance import governance_commands
from dsf.instance.squad_render import KV_SECRET_NAME, render_squad_bundle

Runner = Callable[..., Any]

#: Progress callback: ``(phase, index, total, step, error)`` where ``phase`` is
#: ``"start"`` / ``"done"`` / ``"error"``. ``error`` is set only on ``"error"``.
StepEvent = Callable[[str, int, int, ProvisionStep, "BaseException | None"], None]

#: Cap captured stderr/stdout folded into a step error so a runaway tool log
#: doesn't flood the summary.
_MAX_ERROR_DETAIL = 2000
_READER_ROLE_ID = "acdd72a7-3385-48ef-bd42-f606fba81ae7"
_MONITORING_READER_ROLE_ID = "43d0d8ad-25c7-4714-9337-8ba259a9fe05"
_LOG_ANALYTICS_READER_ROLE_ID = "73c42c96-874c-492b-b04d-ab87d138a893"
_MONITORING_CONTRIBUTOR_ROLE_ID = "749f88d5-cbae-40b8-bcfc-e573ddc772fa"


def _format_step_error(exc: BaseException) -> str:
    """Build a readable error message for a failed step.

    Subprocess steps that capture output (e.g. ``provision_azure``) raise a
    :class:`subprocess.CalledProcessError` whose ``str()`` is only
    ``"Command ... returned non-zero exit status N"`` — the real reason lives in
    the captured ``stderr``/``stdout``. Fold those in so the surfaced error names
    the actual failure (a quota error, an RBAC denial, a bad parameter).
    """
    parts = [str(exc)]
    for attr in ("stderr", "stdout"):
        detail = getattr(exc, attr, None)
        if isinstance(detail, bytes):
            detail = detail.decode(errors="replace")
        detail = (detail or "").strip() if isinstance(detail, str) else ""
        if detail:
            parts.append(detail[:_MAX_ERROR_DETAIL])
    return "\n".join(parts)


def _label_commands(spec: InstanceSpec) -> list[list[str]]:
    """Build idempotent ``gh label create --force`` commands for an instance.

    Creates every label in the product's taxonomy plus the universal
    council->squad :data:`HANDOFF_LABEL` and the SRE->council
    :data:`INCIDENT_LABEL`, so filed issues never fail on a missing label.
    Targets the repo with ``--repo`` (cwd-independent).
    """
    repo = spec.github_repo()
    commands: list[list[str]] = []
    for group in spec.label_taxonomy.values():
        for name in group:
            commands.append(
                ["gh", "label", "create", name, "--repo", repo, "--force"]
            )
    commands.append(
        [
            "gh", "label", "create", HANDOFF_LABEL,
            "--repo", repo,
            "--color", HANDOFF_LABEL_COLOR,
            "--description", HANDOFF_LABEL_DESCRIPTION,
            "--force",
        ]
    )
    commands.append(
        [
            "gh", "label", "create", INCIDENT_LABEL,
            "--repo", repo,
            "--color", INCIDENT_LABEL_COLOR,
            "--description", INCIDENT_LABEL_DESCRIPTION,
            "--force",
        ]
    )
    return commands


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
                name="create_labels",
                description=(
                    f"Create the label taxonomy + handoff label in {s.github_repo()}"
                ),
                commands=_label_commands(s),
            ),
            ProvisionStep(
                name="squad_init",
                description=f"Initialize Coding Squad in {s.github_repo()}",
                command=["squad", "init", "--preset", "default"],
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
                name="register_product",
                description=(
                    f"Register {s.product} -> {s.github_repo()} in the routing "
                    "registry (config/products.json)"
                ),
            ),
            ProvisionStep(
                name="deploy_council",
                description=(
                    f"Render + bring up the feature-council runtime scoped to {s.product}"
                ),
            ),
            ProvisionStep(
                name="deploy_squad_ralph",
                description=(
                    f"Render + apply the Ralph watch loop (AKS + KEDA) for {s.product}"
                ),
            ),
            ProvisionStep(
                name="squad_governance",
                description=(
                    f"Apply the '{s.squad_maturity}' squad maturity dial to "
                    f"{s.github_repo()}"
                ),
                commands=governance_commands(s),
            ),
            ProvisionStep(
                name="deploy_sre_agent",
                description=(
                    f"Provision the Azure SRE Agent for {s.product} "
                    f"(agent + RBAC on {', '.join(s.monitored_rgs())} + Azure Monitor)"
                ),
            ),
            ProvisionStep(
                name="write_config",
                description=f"Write instance manifest to config/instances/{s.product}.json",
            ),
        ]
        return InstancePlan(product=s.product, steps=steps)

    def apply(
        self,
        *,
        execute: bool = False,
        on_event: StepEvent | None = None,
    ) -> InstanceManifest:
        """Apply the plan. ``execute=False`` is a side-effect-free dry-run that
        still writes the manifest; ``execute=True`` runs the non-deferred,
        commanded steps via the injected runner.

        ``on_event`` is an optional progress callback invoked as
        ``on_event(phase, index, total, step, error)`` — ``phase`` is ``"start"``
        before a step runs, ``"done"`` after it succeeds, and ``"error"`` if it
        fails — so a caller can surface live per-step status.

        A step that raises is recorded on that step (``result="failed"``,
        ``error=<message>``) and STOPS the run (later steps are left unrun);
        ``apply`` does not propagate the exception. The manifest is always
        persisted — even on failure — so the randomized ``name_prefix`` survives
        and a retry reuses the same resource names instead of orphaning the first
        attempt. Non-executing runs carry prior provisioning state forward so a
        preview or ``--write-plan`` never blanks recorded Azure outputs.
        """
        emit = on_event or (lambda *_a: None)
        plan = self.plan()
        path = manifest_path(self.spec.product, self._repo_root)
        prior = self._existing_manifest()
        azure_result = prior.azure if (prior and not execute) else None
        executed = execute or bool(prior and prior.executed)
        # write_config is finalized after the manifest is built, not run in-loop.
        steps = [s for s in plan.steps if s.name != "write_config"]
        total = len(steps)
        try:
            for index, step in enumerate(steps, 1):
                emit("start", index, total, step, None)
                try:
                    azure_result = self._execute_step(
                        step,
                        execute=execute,
                        executed=executed,
                        azure_result=azure_result,
                        plan=plan,
                    )
                except Exception as exc:  # noqa: BLE001 - reported via on_event, not swallowed
                    step.executed = False
                    step.result = "failed"
                    step.error = _format_step_error(exc)
                    emit("error", index, total, step, exc)
                    break
                emit("done", index, total, step, None)
        finally:
            for step in plan.steps:
                if step.name == "write_config":
                    step.executed, step.result = True, str(path)
            manifest = InstanceManifest(
                spec=self.spec, plan=plan, executed=executed, azure=azure_result
            )
            write_manifest(manifest, self._repo_root)
        return manifest

    def _execute_step(
        self,
        step: ProvisionStep,
        *,
        execute: bool,
        executed: bool,
        azure_result: AzureProvisionResult | None,
        plan: InstancePlan,
    ) -> AzureProvisionResult | None:
        """Run one provisioning step, mutating ``step.result``/``executed``.

        Returns the (possibly updated) ``azure_result``: ``provision_azure``
        captures the deployment outputs that the council/SRE steps consume.
        """
        if step.deferred:
            step.result = "deferred"
        elif step.name == "register_product":
            render_product_registration(
                InstanceManifest(
                    spec=self.spec, plan=plan, executed=executed, azure=azure_result
                ),
                repo_root=self._repo_root,
            )
            step.executed = execute
            step.result = "registered" if execute else "registered (dry-run)"
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
        elif step.name == "deploy_squad_ralph":
            provisional = InstanceManifest(
                spec=self.spec, plan=plan, executed=executed, azure=azure_result
            )
            bundle = render_squad_bundle(provisional, repo_root=self._repo_root)
            if not execute:
                step.result = "rendered (dry-run)"
            else:
                self._run(
                    [
                        "az", "aks", "get-credentials",
                        "--resource-group", self.spec.resource_group(),
                        "--name", f"aks-dsf-{self.spec.product}",
                        "--overwrite-existing",
                    ],
                    check=True,
                )
                self._seed_github_token(azure_result)
                for manifest_file in (
                    bundle.identity_path,
                    bundle.exporter_path,
                    bundle.deployment_path,
                    bundle.scaledobject_path,
                ):
                    self._run(
                        ["kubectl", "apply", "-f", str(manifest_file)],
                        check=True,
                    )
                step.executed, step.result = True, "deployed"
        elif step.name == "deploy_sre_agent":
            provisional = InstanceManifest(
                spec=self.spec, plan=plan, executed=executed, azure=azure_result
            )
            render_sre_summary(provisional, repo_root=self._repo_root)
            if not execute:
                step.result = "deployed (dry-run)"
            else:
                outputs = azure_result.outputs if azure_result else {}
                self._run(self._sre_deploy_command(outputs), check=True)
                note = self._connect_repo(azure_result)
                step.executed = True
                step.result = note or "deployed"
        elif not execute:
            step.result = "dry-run"
        elif step.commands:
            kwargs = {"cwd": step.cwd} if step.cwd else {}
            for cmd in step.commands:
                self._run(cmd, check=True, **kwargs)
            step.executed, step.result = True, "executed"
        elif not step.command:
            step.result = "noop"
        elif step.name == "create_repo" and self._repo_exists():
            step.executed = True
            repo_dir = self.spec.resolved_repo()
            if Path(repo_dir).is_dir():
                step.result = "exists"
            else:
                # Repo exists remotely but isn't cloned here; the squad steps
                # need a local working copy, so clone it.
                self._run(
                    ["gh", "repo", "clone", self.spec.github_repo(), repo_dir],
                    check=True,
                )
                step.result = "cloned"
        elif step.name == "provision_azure":
            proc = self._run(step.command, check=True, capture_output=True, text=True)
            azure_result = self._azure_result(proc)
            step.executed, step.result = True, "executed"
        else:
            kwargs = {"cwd": step.cwd} if step.cwd else {}
            self._run(step.command, check=True, **kwargs)
            step.executed, step.result = True, "executed"
        return azure_result

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

    def _sre_deploy_command(self, outputs: dict[str, str]) -> list[str]:
        """`az deployment sub create` for the SRE agent, from provision_azure outputs."""
        s = self.spec
        bicep = str((self._repo_root or _default_repo_root()) / "infra" / "sre-agent.bicep")
        target_rgs = json.dumps(s.monitored_rgs())
        return [
            "az", "deployment", "sub", "create",
            "-l", s.sre_agent_location,
            "-n", f"dsf-sre-{s.product}",
            "-f", bicep,
            "-p",
            f"product={s.product}",
            f"agentName={s.sre_agent_name()}",
            f"sreAgentLocation={s.sre_agent_location}",
            f"agentResourceGroup={s.sre_resource_group()}",
            f"targetResourceGroups={target_rgs}",
            f"appInsightsId={outputs.get('appInsightsId', '')}",
            f"logAnalyticsId={outputs.get('logAnalyticsId', '')}",
            "permissionLevel=Reader",
            "--query", "properties.outputs", "-o", "json",
        ]

    def _connect_repo(self, azure_result: AzureProvisionResult | None) -> str:
        """Best-effort Phase-2 data-plane repo connect via `az rest`.

        Returns a short skip note when the agent endpoint or a GitHub token is
        unavailable, so a missing data-plane path never fails the whole provision.
        """
        endpoint = azure_result.outputs.get("agentEndpoint", "") if azure_result else ""
        if not endpoint:
            return "deployed; repo connect skipped (no agent endpoint)"
        token = self._run(["gh", "auth", "token"], check=False, capture_output=True, text=True)
        gh_token = getattr(token, "stdout", "").strip()
        if not gh_token:
            return "deployed; repo connect skipped (no GitHub token)"
        body = json.dumps({"repository": self.spec.github_repo(), "token": gh_token})
        self._run(
            [
                "az", "rest", "--method", "post",
                "--url", f"{endpoint}/repositories",
                "--resource", "https://azuresre.dev",
                "--headers", "Content-Type=application/json",
                "--body", body,
            ],
            check=False,
        )
        return "deployed; repo connected"

    def _seed_github_token(self, azure_result: AzureProvisionResult | None) -> None:
        """Seed the operator's GitHub token into the per-product Key Vault.

        The squad pods never hold a static in-cluster credential; the Key Vault
        CSI driver projects this secret at runtime under AKS workload identity
        (Option 2, ADR 0012). Swapping to a GitHub App installation token later is
        a value change here, not a rewrite. Reads the vault name from the Azure
        deployment outputs captured by the ``provision_azure`` step.
        """
        keyvault_name = azure_result.outputs.get("keyVaultName", "") if azure_result else ""
        token = self._run(
            ["gh", "auth", "token"], check=True, capture_output=True, text=True
        )
        self._run(
            [
                "az", "keyvault", "secret", "set",
                "--vault-name", keyvault_name,
                "--name", KV_SECRET_NAME,
                "--value", getattr(token, "stdout", "").strip(),
            ],
            check=True,
        )


class InstanceOffboarder:
    """Build and apply the shared teardown core for one product instance."""

    def __init__(
        self,
        product: str,
        *,
        run: Runner | None = None,
        repo_root: Path | None = None,
        purge: bool = False,
    ) -> None:
        self.product = product
        self._run = run or subprocess.run
        self._repo_root = repo_root
        self._purge = purge
        self._manifest: InstanceManifest | None = None

    def plan(self) -> InstancePlan:
        spec = self._load_manifest().spec
        purge_step = ProvisionStep(
            name="purge_soft_deleted",
            description=(
                "Purge soft-deleted Key Vault + Foundry/Cognitive accounts for name reuse"
                if self._purge
                else "Skip soft-delete purge (enable with --purge)"
            ),
            deferred=not self._purge,
        )
        return InstancePlan(
            product=self.product,
            steps=[
                ProvisionStep(
                    name="remove_sre_rbac",
                    description=(
                        f"Remove SRE agent RBAC outside {spec.sre_resource_group()} "
                        "(cross-RG + subscription)"
                    ),
                ),
                ProvisionStep(
                    name="delete_sre_resource_group",
                    description=f"Delete dedicated SRE resource group {spec.sre_resource_group()}",
                    command=["az", "group", "delete", "--name", spec.sre_resource_group(), "--yes"],
                ),
                ProvisionStep(
                    name="delete_product_resource_group",
                    description=f"Delete product resource group {spec.resource_group()}",
                    command=["az", "group", "delete", "--name", spec.resource_group(), "--yes"],
                ),
                purge_step,
                ProvisionStep(
                    name="unregister_product",
                    description=f"Unregister {self.product} from config/products.json",
                ),
                ProvisionStep(
                    name="remove_instance_artifacts",
                    description=(
                        f"Remove config/instances/{self.product}.json and "
                        f"config/instances/{self.product}.runtime/"
                    ),
                ),
            ],
        )

    def apply(
        self,
        *,
        execute: bool = False,
        on_event: StepEvent | None = None,
    ) -> InstancePlan:
        emit = on_event or (lambda *_a: None)
        plan = self.plan()
        total = len(plan.steps)
        for index, step in enumerate(plan.steps, 1):
            emit("start", index, total, step, None)
            try:
                self._execute_step(step, execute=execute)
            except Exception as exc:  # noqa: BLE001
                step.executed = False
                step.result = "failed"
                step.error = _format_step_error(exc)
                emit("error", index, total, step, exc)
                break
            emit("done", index, total, step, None)
        return plan

    def _execute_step(self, step: ProvisionStep, *, execute: bool) -> None:
        if step.deferred:
            step.result = "skipped"
        elif not execute:
            step.result = "dry-run"
        elif step.name == "remove_sre_rbac":
            step.executed = True
            step.result = self._remove_sre_rbac()
        elif step.name == "delete_sre_resource_group":
            step.executed = True
            step.result = self._delete_group(self._load_manifest().spec.sre_resource_group())
        elif step.name == "delete_product_resource_group":
            step.executed = True
            step.result = self._delete_group(self._load_manifest().spec.resource_group())
        elif step.name == "purge_soft_deleted":
            step.executed = True
            step.result = self._purge_soft_deleted()
        elif step.name == "unregister_product":
            render_product_unregistration(self.product, repo_root=self._repo_root)
            step.executed = True
            step.result = "unregistered"
        elif step.name == "remove_instance_artifacts":
            mpath = manifest_path(self.product, self._repo_root)
            mpath.unlink(missing_ok=True)
            shutil.rmtree(runtime_dir(self.product, self._repo_root), ignore_errors=True)
            step.executed = True
            step.result = "removed"
        else:
            step.result = "noop"

    def _load_manifest(self) -> InstanceManifest:
        if self._manifest is not None:
            return self._manifest
        path = manifest_path(self.product, self._repo_root)
        if not path.exists():
            raise FileNotFoundError(
                f"Instance manifest not found: {path}. Offboard requires "
                f"config/instances/{self.product}.json."
            )
        self._manifest = read_manifest(self.product, self._repo_root)
        return self._manifest

    def _delete_group(self, name: str) -> str:
        if not self._group_exists(name):
            return "already absent"
        try:
            self._run(["az", "group", "delete", "--name", name, "--yes"], check=True)
        except subprocess.CalledProcessError as exc:
            if self._is_not_found_error(exc):
                return "already absent"
            raise
        return "deleted"

    def _group_exists(self, name: str) -> bool:
        proc = self._run(
            ["az", "group", "exists", "--name", name],
            check=False,
            capture_output=True,
            text=True,
        )
        return str(getattr(proc, "stdout", "")).strip().lower() == "true"

    def _remove_sre_rbac(self) -> str:
        spec = self._load_manifest().spec
        principal_id = self._capture_tsv(
            [
                "az", "identity", "show",
                "--resource-group", spec.sre_resource_group(),
                "--name", f"{spec.sre_agent_name()}-id",
                "--query", "principalId",
                "-o", "tsv",
            ],
            allow_not_found=True,
        )
        if not principal_id:
            return "already absent"
        sub_id = self._capture_tsv(["az", "account", "show", "--query", "id", "-o", "tsv"])
        rg_scopes = [f"/subscriptions/{sub_id}/resourceGroups/{rg}" for rg in spec.monitored_rgs()]
        for scope in rg_scopes:
            self._delete_role_assignment(principal_id, _READER_ROLE_ID, scope)
            self._delete_role_assignment(principal_id, _MONITORING_READER_ROLE_ID, scope)
            self._delete_role_assignment(principal_id, _LOG_ANALYTICS_READER_ROLE_ID, scope)
        self._delete_role_assignment(
            principal_id,
            _MONITORING_CONTRIBUTOR_ROLE_ID,
            f"/subscriptions/{sub_id}",
        )
        return "removed"

    def _delete_role_assignment(self, principal_id: str, role_id: str, scope: str) -> None:
        cmd = [
            "az", "role", "assignment", "delete",
            "--assignee-object-id", principal_id,
            "--assignee-principal-type", "ServicePrincipal",
            "--role", role_id,
            "--scope", scope,
        ]
        proc = self._run(cmd, check=False, capture_output=True, text=True)
        if getattr(proc, "returncode", 1) == 0:
            return
        detail = f"{getattr(proc, 'stderr', '')}\n{getattr(proc, 'stdout', '')}"
        if self._is_not_found_text(detail):
            return
        raise subprocess.CalledProcessError(
            getattr(proc, "returncode", 1),
            cmd,
            output=getattr(proc, "stdout", ""),
            stderr=getattr(proc, "stderr", ""),
        )

    def _purge_soft_deleted(self) -> str:
        manifest = self._load_manifest()
        spec = manifest.spec
        outputs = manifest.azure.outputs if manifest.azure else {}
        keyvault_name = outputs.get("keyVaultName", "")
        keyvault_purged = self._purge_keyvault_if_deleted(keyvault_name, spec.location)

        foundry_names = set()
        for key in ("foundryAccountName", "cognitiveAccountName", "aiFoundryName"):
            value = outputs.get(key, "")
            if value:
                foundry_names.add(value)
        foundry_names.update(self._list_deleted_cognitive_accounts(spec.name_prefix))
        purged_foundry = 0
        for name in sorted(foundry_names):
            if self._purge_cognitive_if_deleted(name, spec.location):
                purged_foundry += 1

        return (
            f"purged (keyvault={'yes' if keyvault_purged else 'no'}, "
            f"foundry={purged_foundry})"
        )

    def _purge_keyvault_if_deleted(self, name: str, location: str) -> bool:
        if not name:
            return False
        found = self._capture_tsv(
            ["az", "keyvault", "list-deleted", "--query", f"[?name=='{name}'].name", "-o", "tsv"],
            allow_not_found=True,
        )
        if not found:
            return False
        self._run(
            ["az", "keyvault", "purge", "--name", name, "--location", location],
            check=True,
        )
        return True

    def _list_deleted_cognitive_accounts(self, name_prefix: str) -> list[str]:
        listed = self._capture_tsv(
            [
                "az", "cognitiveservices", "account", "list-deleted",
                "--query", f"[?starts_with(name, '{name_prefix}')].name",
                "-o", "tsv",
            ],
            allow_not_found=True,
        )
        return [line.strip() for line in listed.splitlines() if line.strip()]

    def _purge_cognitive_if_deleted(self, name: str, location: str) -> bool:
        if not name:
            return False
        found = self._capture_tsv(
            [
                "az",
                "cognitiveservices",
                "account",
                "list-deleted",
                "--query",
                f"[?name=='{name}'].name",
                "-o",
                "tsv",
            ],
            allow_not_found=True,
        )
        if not found:
            return False
        self._run(
            ["az", "cognitiveservices", "account", "purge", "--name", name, "--location", location],
            check=True,
        )
        return True

    def _capture_tsv(self, cmd: list[str], *, allow_not_found: bool = False) -> str:
        proc = self._run(cmd, check=False, capture_output=True, text=True)
        if getattr(proc, "returncode", 1) == 0:
            return str(getattr(proc, "stdout", "")).strip()
        detail = f"{getattr(proc, 'stderr', '')}\n{getattr(proc, 'stdout', '')}"
        if allow_not_found and self._is_not_found_text(detail):
            return ""
        raise subprocess.CalledProcessError(
            getattr(proc, "returncode", 1),
            cmd,
            output=getattr(proc, "stdout", ""),
            stderr=getattr(proc, "stderr", ""),
        )

    def _is_not_found_error(self, exc: BaseException) -> bool:
        return self._is_not_found_text(_format_step_error(exc))

    @staticmethod
    def _is_not_found_text(text: str) -> bool:
        value = text.lower()
        return any(
            marker in value
            for marker in (
                "not found",
                "does not exist",
                "could not be found",
                "resourcegroupnotfound",
                "resourcenotfound",
                "missing",
                "wasn't found",
                "couldn't be found",
            )
        )
