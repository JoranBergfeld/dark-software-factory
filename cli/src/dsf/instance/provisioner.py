"""InstanceProvisioner — build and apply a provisioning plan for an instance.

External CLIs (``gh``, ``az``) are invoked through an injectable
``run`` callable (defaults to :func:`subprocess.run`) so tests stay offline,
mirroring :class:`dsf.github_client.RealGitHubClient`.
"""

from __future__ import annotations

import json
import shutil
import subprocess
import tempfile
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
from dsf.instance.branch_protection import (
    RULESET_NAME,
    RULESET_UNSUPPORTED_RESULT,
    auto_merge_command,
    is_unsupported_ruleset_error,
    ruleset_payload,
)
from dsf.instance.deploy_progress import DeploymentProgressPoller
from dsf.instance.runtime_render import (
    render_product_registration,
    render_product_unregistration,
    render_runtime_bundle,
    render_sre_summary,
    runtime_dir,
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
from dsf.instance.teardown_common import (
    ALREADY_ABSENT_RESULT,
    is_not_found,
    is_not_found_text,
)

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
                    f"githubAppId={self._github_app_id}",
                    f"githubInstallationId={self._github_installation_id}",
                    f"githubRepository={s.github_repo()}",
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
                name="seed_app_key",
                description=(
                    "Seed the DSF App private key from the owner Key Vault into the "
                    f"product Key Vault for {s.product}"
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
                name="branch_protection",
                description=(
                    f"Apply the '{s.creation_maturity}' creation maturity dial to "
                    f"{s.github_repo()} as a branch-protection ruleset (required "
                    "reviews + green 'ci' check)"
                ),
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
        # Carry a previously recorded App binding forward so a skip/preview/--write-plan
        # never blanks it; install_app overwrites self._app_binding only when it installs.
        self._app_binding = prior.github_app if prior else None
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
        elif step.name == "seed_app_key":
            if not self._owner_keyvault_uri:
                step.result = "skipped (no owner App configured)"
            elif not execute:
                step.result = "seeded (dry-run)"
            else:
                self._seed_app_key(azure_result)
                step.executed, step.result = True, "seeded"
        elif step.name == "install_app":
            if not self._owner_keyvault_uri:
                step.result = "skipped (no owner App configured)"
            elif not execute:
                step.result = "installed (dry-run)"
            elif not self._github_installation_id:
                step.result = "skipped (owner App pointer unavailable)"
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
        elif step.name == "branch_protection":
            if not execute:
                step.result = "ruleset planned (dry-run)"
            else:
                skip_reason = self._apply_branch_protection()
                if skip_reason:
                    step.result = skip_reason
                else:
                    step.executed, step.result = True, "applied"
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

    def _apply_branch_protection(self) -> str | None:
        """Apply the creation-maturity dial as a real branch-protection ruleset.

        Uses the operator's interactive ``gh`` auth (admin on the just-created repo),
        not the App, so the App needs no ``administration:write`` scope. Idempotent:
        updates the existing ``dsf-creation`` ruleset in place when present. The
        ruleset JSON is passed on stdin (``gh api --input -``) — no temp files.
        """
        repo = self.spec.github_repo()
        payload = json.dumps(ruleset_payload(self.spec))
        try:
            lookup = self._run(
                [
                    "gh", "api", f"/repos/{repo}/rulesets?includes_parents=false", "--jq",
                    f'[.[] | select(.name=="{RULESET_NAME}") | .id] | first // empty',
                ],
                check=True, capture_output=True, text=True,
            )
        except subprocess.CalledProcessError as exc:
            if is_unsupported_ruleset_error(getattr(exc, "stderr", "") or ""):
                return RULESET_UNSUPPORTED_RESULT
            raise
        ruleset_id = (getattr(lookup, "stdout", "") or "").strip()
        if ruleset_id:
            self._run(
                ["gh", "api", "--method", "PUT",
                 f"/repos/{repo}/rulesets/{ruleset_id}", "--input", "-"],
                input=payload, text=True, check=True,
            )
        else:
            self._run(
                ["gh", "api", "--method", "POST",
                 f"/repos/{repo}/rulesets", "--input", "-"],
                input=payload, text=True, check=True,
            )
        self._run(auto_merge_command(self.spec), check=True)
        return None

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

    def _seed_app_key(self, azure_result: AzureProvisionResult | None) -> None:
        """Copy the App private key from the owner KV into the product KV (with retry).

        Mirrors ``_seed_appconfig``: the product Key Vault's Secrets User/Officer role
        grant and this data-plane write would otherwise race, so the (idempotent) set
        is retried until the grant propagates. The PEM is materialized to a 0600 temp
        file only long enough to push it in, then unlinked; the owner-vault read and the
        product-vault write both capture output so the key is never echoed.
        """
        if not self._owner_keyvault_uri:
            raise RuntimeError(
                "no owner Key Vault configured; set DSF_OWNER_KEYVAULT_URI or "
                "--owner-keyvault-uri (run `dsf bootstrap` first)"
            )
        product_kv = azure_result.outputs.get("keyVaultName", "") if azure_result else ""
        if not product_kv:
            raise RuntimeError("provision_azure returned no keyVaultName; cannot seed App key")
        owner_kv = self._owner_keyvault_uri.split("//", 1)[-1].split(".", 1)[0]

        pem = self._run(
            [
                "az", "keyvault", "secret", "show", "--vault-name", owner_kv,
                "--name", "github-app-private-key", "--query", "value", "-o", "tsv",
            ],
            check=True,
            capture_output=True,
            text=True,
        )
        fd, pem_path = tempfile.mkstemp(prefix="dsf-app-", suffix=".pem")
        try:
            with open(fd, "w", encoding="utf-8") as fh:
                fh.write(getattr(pem, "stdout", ""))
            Path(pem_path).chmod(0o600)
            cmd = [
                "az", "keyvault", "secret", "set", "--vault-name", product_kv,
                "--name", "github-app-private-key", "--file", pem_path,
            ]
            last_error: subprocess.CalledProcessError | None = None
            for attempt in range(1, _SEED_MAX_ATTEMPTS + 1):
                try:
                    self._run(cmd, check=True, capture_output=True, text=True)
                    return
                except subprocess.CalledProcessError as exc:
                    last_error = exc
                    if attempt < _SEED_MAX_ATTEMPTS:
                        self._sleep(_SEED_RETRY_DELAY)
            assert last_error is not None
            raise last_error
        finally:
            Path(pem_path).unlink(missing_ok=True)


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
        """Apply the teardown plan, stopping at the first failed step.

        Already-absent resources (404s) are tolerated: the step records
        ``"not-found (already absent)"`` and the line continues, so a re-run
        can finish a partial offboard.
        """
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
            return ALREADY_ABSENT_RESULT
        try:
            self._run(["az", "group", "delete", "--name", name, "--yes"], check=True)
        except subprocess.CalledProcessError as exc:
            if is_not_found(exc):
                return ALREADY_ABSENT_RESULT
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
            return ALREADY_ABSENT_RESULT
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
        if is_not_found_text(detail):
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
        try:
            self._run(
                ["az", "keyvault", "purge", "--name", name, "--location", location],
                check=True,
            )
        except subprocess.CalledProcessError as exc:
            if is_not_found(exc):
                return False
            raise
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
        try:
            self._run(
                [
                    "az", "cognitiveservices", "account", "purge",
                    "--name", name, "--location", location,
                ],
                check=True,
            )
        except subprocess.CalledProcessError as exc:
            if is_not_found(exc):
                return False
            raise
        return True

    def _capture_tsv(self, cmd: list[str], *, allow_not_found: bool = False) -> str:
        proc = self._run(cmd, check=False, capture_output=True, text=True)
        if getattr(proc, "returncode", 1) == 0:
            return str(getattr(proc, "stdout", "")).strip()
        detail = f"{getattr(proc, 'stderr', '')}\n{getattr(proc, 'stdout', '')}"
        if allow_not_found and is_not_found_text(detail):
            return ""
        raise subprocess.CalledProcessError(
            getattr(proc, "returncode", 1),
            cmd,
            output=getattr(proc, "stdout", ""),
            stderr=getattr(proc, "stderr", ""),
        )
