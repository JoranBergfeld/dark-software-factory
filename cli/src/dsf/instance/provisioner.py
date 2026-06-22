"""InstanceProvisioner — build and apply a provisioning plan for an instance.

External CLIs (``gh``, ``az``) are invoked through an injectable
``run`` callable (defaults to :func:`subprocess.run`) so tests stay offline,
mirroring :class:`dsf.github_client.RealGitHubClient`.
"""

from __future__ import annotations

import json
import subprocess
import time
from collections.abc import Callable, Iterator
from pathlib import Path
from typing import Any

from dsf.config.store import load_defaults
from dsf.contracts.handoff import (
    HANDOFF_LABEL,
    HANDOFF_LABEL_COLOR,
    HANDOFF_LABEL_DESCRIPTION,
    INCIDENT_LABEL,
    INCIDENT_LABEL_COLOR,
    INCIDENT_LABEL_DESCRIPTION,
)
from dsf.instance.deploy_progress import DeploymentProgressPoller
from dsf.instance.runtime_render import (
    render_product_registration,
    render_runtime_bundle,
    render_sre_summary,
)
from dsf.instance.spec import (
    AzureProvisionResult,
    GitHubAppBinding,
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

Runner = Callable[..., Any]

#: Progress callback: ``(phase, index, total, step, error)`` where ``phase`` is
#: ``"start"`` / ``"done"`` / ``"error"``. ``error`` is set only on ``"error"``.
StepEvent = Callable[[str, int, int, ProvisionStep, "BaseException | None"], None]

#: Cap captured stderr/stdout folded into a step error so a runaway tool log
#: doesn't flood the summary.
_MAX_ERROR_DETAIL = 2000

#: App Configuration seeding (``seed_appconfig`` step) retries the batch to absorb
#: the role-assignment (Data Owner) RBAC propagation lag right after deployment:
#: the first ``az appconfig kv set`` fails fast with ``Forbidden`` until the grant
#: lands, then the whole batch succeeds. Bound: ~2 min of propagation tolerance.
_SEED_MAX_ATTEMPTS = 8
_SEED_RETRY_DELAY = 15.0


def _flatten_config(node: Any, prefix: str = "") -> Iterator[tuple[str, Any]]:
    """Yield ``(dotted_key, value)`` leaves of a nested config ``dict``.

    Mirrors how :class:`dsf.config.azure_store.AppConfigStore` reads config: a
    flat App Configuration key equals the dotted path the in-memory store walks
    (``critics.security.enabled``), and lists (e.g. ``jury.roster``) are leaves.
    """
    if isinstance(node, dict):
        for key, value in node.items():
            child = f"{prefix}.{key}" if prefix else key
            yield from _flatten_config(value, child)
    else:
        yield prefix, node


def _appconfig_seed_commands(endpoint: str) -> list[list[str]]:
    """Build ``az appconfig kv set`` commands seeding the canonical defaults.

    Seeds the flattened ``config/defaults.json`` into App Configuration so the
    Azure runtime resolves the same critic/agent/threshold config the in-memory
    store does (an empty store would read every ``critic.*``/``agent.*`` flag as
    disabled). Values are JSON-encoded because the store ``json.loads`` them;
    keys are unlabelled (the baseline that per-product labels override). Uses
    ``--auth-mode login`` so the deployer's AAD identity (granted Data Owner)
    writes the values under ``disableLocalAuth``.
    """
    commands: list[list[str]] = []
    for key, value in _flatten_config(load_defaults()):
        commands.append(
            [
                "az", "appconfig", "kv", "set",
                "--endpoint", endpoint,
                "--auth-mode", "login",
                "--key", key,
                "--value", json.dumps(value),
                "--yes",
            ]
        )
    return commands


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
        sleep: Callable[[float], None] | None = None,
        owner_keyvault_uri: str = "",
        github_app_id: str = "",
        github_installation_id: str = "",
    ) -> None:
        self.spec = spec
        self._run = run or subprocess.run
        self._repo_root = repo_root
        self._sleep = sleep or time.sleep
        self._owner_keyvault_uri = owner_keyvault_uri
        self._github_app_id = github_app_id
        self._github_installation_id = github_installation_id
        self._app_binding: GitHubAppBinding | None = None

    def plan(self) -> InstancePlan:
        """Return the ordered provisioning plan (pure — no side effects)."""
        s = self.spec
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
                name="install_app",
                description=(
                    f"Add {s.github_repo()} to the DSF App installation "
                    f"{self._github_installation_id or '<installation>'}"
                ),
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
                    "--no-wait",
                ],
            ),
            ProvisionStep(
                name="seed_appconfig",
                description=(
                    "Seed the canonical config/defaults.json into App Configuration "
                    f"for {s.product} (critic/agent flags + thresholds)"
                ),
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
        on_progress: Callable[[str], None] | None = None,
    ) -> InstanceManifest:
        """Apply the plan. ``execute=False`` is a side-effect-free dry-run that
        still writes the manifest; ``execute=True`` runs the non-deferred,
        commanded steps via the injected runner.

        ``on_event`` is an optional progress callback invoked as
        ``on_event(phase, index, total, step, error)`` — ``phase`` is ``"start"``
        before a step runs, ``"done"`` after it succeeds, and ``"error"`` if it
        fails — so a caller can surface live per-step status.
        ``on_progress`` receives live per-resource lines while
        ``provision_azure`` polls its deployment.

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
                        on_progress=on_progress,
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
                spec=self.spec, plan=plan, executed=executed,
                azure=azure_result, github_app=self._app_binding,
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
        on_progress: Callable[[str], None] | None = None,
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
        elif step.name == "seed_appconfig":
            if not execute:
                step.result = "seeded (dry-run)"
            else:
                self._seed_appconfig(azure_result)
                step.executed, step.result = True, "seeded"
        elif step.name == "install_app":
            if not self._github_installation_id:
                step.result = "skipped (no owner App configured)"
            elif not execute:
                step.result = "installed (dry-run)"
            else:
                self._app_binding = self._install_app()
                step.executed, step.result = True, "installed"
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
                # Repo exists remotely but isn't cloned here; later steps need a
                # local working copy, so clone it.
                self._run(
                    ["gh", "repo", "clone", self.spec.github_repo(), repo_dir],
                    check=True,
                )
                step.result = "cloned"
        elif step.name == "provision_azure":
            # Kick off the deployment asynchronously, then poll its operations so
            # each resource streams to the console; outputs are read post-deploy.
            self._run(step.command, check=True, capture_output=True, text=True)
            poller = DeploymentProgressPoller(
                run=self._run, sleep=self._sleep, emit=on_progress
            )
            outputs = poller.stream(self.spec.resource_group(), self.spec.deployment_name())
            azure_result = self._azure_result_from_outputs(outputs)
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

    def _azure_result_from_outputs(self, parsed: Any) -> AzureProvisionResult:
        """Map an ``az deployment ... --query properties.outputs`` dict to a result.

        Unwraps each ``{"type": ..., "value": ...}`` envelope to its value; fed by the
        ``provision_azure`` poller's post-deploy ``show`` outputs.
        """
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

    def _install_app(self) -> GitHubAppBinding:
        """Add the product repo to the single owner installation; capture the binding."""
        repo = self.spec.github_repo()
        lookup = self._run(
            ["gh", "api", f"/repos/{repo}", "--jq", ".id"],
            check=True, capture_output=True, text=True,
        )
        repository_id = int(getattr(lookup, "stdout", "0").strip())
        self._run(
            [
                "gh", "api", "--method", "PUT",
                f"/user/installations/{self._github_installation_id}"
                f"/repositories/{repository_id}",
            ],
            check=True,
        )
        return GitHubAppBinding(
            app_id=self._github_app_id,
            installation_id=self._github_installation_id,
            repository_id=repository_id,
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

    def _seed_appconfig(self, azure_result: AzureProvisionResult | None) -> None:
        """Seed the canonical config defaults into App Configuration (with retry).

        Bicep provisions App Configuration control-plane only (``disableLocalAuth``
        + a Data Owner grant to the deployer); the key-values are written here,
        post-deploy, from the deployer's AAD identity. Because that role grant and
        these writes would otherwise race in a single ARM deployment, the batch is
        retried: the first ``az appconfig kv set`` fails fast with ``Forbidden``
        until the grant propagates, then the whole (idempotent) batch succeeds.

        Reads the App Configuration endpoint from the ``provision_azure`` outputs.
        """
        endpoint = azure_result.outputs.get("appConfigEndpoint", "") if azure_result else ""
        if not endpoint:
            raise RuntimeError(
                "provision_azure returned no appConfigEndpoint; cannot seed App Configuration"
            )
        commands = _appconfig_seed_commands(endpoint)
        last_error: subprocess.CalledProcessError | None = None
        for attempt in range(1, _SEED_MAX_ATTEMPTS + 1):
            try:
                for cmd in commands:
                    self._run(cmd, check=True, capture_output=True, text=True)
                return
            except subprocess.CalledProcessError as exc:
                last_error = exc
                if attempt < _SEED_MAX_ATTEMPTS:
                    self._sleep(_SEED_RETRY_DELAY)
        assert last_error is not None  # loop ran at least once
        raise last_error
