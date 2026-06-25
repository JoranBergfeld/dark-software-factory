"""Shared teardown helpers for the ``dsf delete`` and ``dsf offboard`` paths.

Both teardown paths — ``dsf delete``
(:class:`dsf.instance.deprovisioner.InstanceDeprovisioner`) and ``dsf offboard``
(:class:`dsf.instance.provisioner.InstanceOffboarder`) — must treat an
already-gone resource as success so a re-run can finish a partial teardown, must
remove the SRE agent's role assignments that live outside its resource group, and
must never delete a resource group that is not tagged ``managed-by=dsf`` (which
would risk deleting a foreign group that merely shares a name). They share one
not-found classifier, one tag-guarded delete, and one :class:`AzureTeardown`
helper here instead of each carrying a copy.
"""

from __future__ import annotations

import json
import subprocess
from collections.abc import Callable
from typing import TYPE_CHECKING, Any

from dsf.instance.tagging import MANAGED_BY_TAG, MANAGED_BY_VALUE

if TYPE_CHECKING:
    from dsf.instance.spec import InstanceSpec

Runner = Callable[..., Any]

#: Built-in Azure RBAC role definition IDs granted to the SRE agent's managed
#: identity. They must be removed on teardown — notably the subscription-scoped
#: Monitoring Contributor, which otherwise orphans a subscription-level assignment
#: pointing at a deleted principal.
_READER_ROLE_ID = "acdd72a7-3385-48ef-bd42-f606fba81ae7"
_MONITORING_READER_ROLE_ID = "43d0d8ad-25c7-4714-9337-8ba259a9fe05"
_LOG_ANALYTICS_READER_ROLE_ID = "73c42c96-874c-492b-b04d-ab87d138a893"
_MONITORING_CONTRIBUTOR_ROLE_ID = "749f88d5-cbae-40b8-bcfc-e573ddc772fa"

#: Result recorded when a teardown target is already gone (404), so a re-run of a
#: partial teardown finishes instead of failing on an absent resource.
ALREADY_ABSENT_RESULT = "not-found (already absent)"

#: stderr/stdout signals that indicate a resource is already absent (idempotency).
#: Bare ``"missing"`` is intentionally excluded: it is too broad (e.g. "missing
#: required argument") and would silently swallow real teardown failures.
NOT_FOUND_SIGNALS = (
    "resourcegroupnotfound",
    "resourcenotfound",
    "could not resolve to a repository",
    "not found",
    "does not exist",
    "no resource group",
    "could not be found",
    "couldn't be found",
    "wasn't found",
)


class ForeignResourceGroupError(RuntimeError):
    """Raised when a teardown target resource group is not tagged ``managed-by=dsf``.

    Guards against deleting a foreign resource group that merely shares a name
    with a DSF instance.
    """


def is_not_found_text(text: str) -> bool:
    """Return ``True`` if ``text`` indicates an already-absent resource (404)."""
    value = text.lower()
    return any(sig in value for sig in NOT_FOUND_SIGNALS)


def is_not_found(exc: subprocess.CalledProcessError) -> bool:
    """Return ``True`` if a failed CLI call indicates an already-absent resource."""
    combined = ""
    for attr in ("stderr", "stdout"):
        raw = getattr(exc, attr, None)
        if isinstance(raw, bytes):
            raw = raw.decode(errors="replace")
        combined += (raw or "").lower()
    return is_not_found_text(combined)


def group_tags(name: str, run: Runner) -> dict[str, str] | None:
    """Return a resource group's tags, or ``None`` when the group is absent.

    Reads ``az group show --query tags``; a not-found (404) error is reported as
    ``None`` (absent) rather than raised, so callers can stay idempotent.
    """
    proc = run(
        ["az", "group", "show", "--name", name, "--query", "tags", "-o", "json"],
        check=False,
        capture_output=True,
        text=True,
    )
    returncode = getattr(proc, "returncode", 0)
    stdout = str(getattr(proc, "stdout", "") or "")
    stderr = str(getattr(proc, "stderr", "") or "")
    if returncode != 0:
        if is_not_found_text(stdout + stderr):
            return None
        raise subprocess.CalledProcessError(
            returncode,
            ["az", "group", "show", "--name", name],
            output=stdout,
            stderr=stderr,
        )
    text = stdout.strip()
    if not text or text == "null":
        return {}
    try:
        data = json.loads(text)
    except json.JSONDecodeError:
        return {}
    return data if isinstance(data, dict) else {}


def guarded_group_delete(name: str, run: Runner) -> str:
    """Delete a resource group only when it is tagged ``managed-by=dsf``.

    - Group absent -> :data:`ALREADY_ABSENT_RESULT` (tolerated; a re-run can
      finish a partial teardown).
    - Group exists but is not tagged ``managed-by=dsf`` ->
      :class:`ForeignResourceGroupError` (refuse; never delete foreign groups).
    - Group tagged ``managed-by=dsf`` -> ``az group delete --yes`` (a concurrent
      404 is tolerated), returning ``"deleted"``.
    """
    tags = group_tags(name, run)
    if tags is None:
        return ALREADY_ABSENT_RESULT
    actual = tags.get(MANAGED_BY_TAG)
    if actual != MANAGED_BY_VALUE:
        raise ForeignResourceGroupError(
            f"refusing to delete resource group {name!r}: it is not tagged "
            f"{MANAGED_BY_TAG}={MANAGED_BY_VALUE} (found {actual!r}). DSF only "
            "deletes resource groups it created."
        )
    try:
        run(["az", "group", "delete", "--name", name, "--yes"], check=True)
    except subprocess.CalledProcessError as exc:
        if is_not_found(exc):
            return ALREADY_ABSENT_RESULT
        raise
    return "deleted"


class AzureTeardown:
    """Azure-CLI teardown operations shared by ``dsf delete`` and ``dsf offboard``.

    Both teardown paths must delete resource groups idempotently and remove the SRE
    agent's role assignments that live *outside* its resource group. Centralising
    that here keeps a single implementation (and one set of role-definition IDs)
    instead of a copy per path.

    ``run`` is a ``subprocess.run``-compatible callable, injected so tests stay
    offline.
    """

    def __init__(self, run: Runner) -> None:
        self._run = run

    # ------------------------------------------------------------------
    # Resource groups
    # ------------------------------------------------------------------

    def delete_group(self, name: str) -> str:
        """Delete a resource group, refusing foreign groups and tolerating absence.

        Delegates to :func:`guarded_group_delete` so the ``managed-by=dsf`` tag
        guard applies on both teardown paths: a missing group reports
        :data:`ALREADY_ABSENT_RESULT`, an untagged group raises
        :class:`ForeignResourceGroupError`, and a tagged group is deleted.
        """
        return guarded_group_delete(name, self._run)

    # ------------------------------------------------------------------
    # SRE RBAC
    # ------------------------------------------------------------------

    def remove_sre_rbac(self, spec: InstanceSpec) -> str:
        """Remove the SRE managed identity's role assignments outside its RG.

        Covers the cross-RG Reader / Monitoring Reader / Log Analytics Reader
        grants on every monitored resource group plus the subscription-scoped
        Monitoring Contributor. Returns :data:`ALREADY_ABSENT_RESULT` when the
        identity itself is already gone.
        """
        principal_id = self.capture_tsv(
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
        sub_id = self.capture_tsv(["az", "account", "show", "--query", "id", "-o", "tsv"])
        rg_scopes = [f"/subscriptions/{sub_id}/resourceGroups/{rg}" for rg in spec.monitored_rgs()]
        for scope in rg_scopes:
            self.delete_role_assignment(principal_id, _READER_ROLE_ID, scope)
            self.delete_role_assignment(principal_id, _MONITORING_READER_ROLE_ID, scope)
            self.delete_role_assignment(principal_id, _LOG_ANALYTICS_READER_ROLE_ID, scope)
        self.delete_role_assignment(
            principal_id,
            _MONITORING_CONTRIBUTOR_ROLE_ID,
            f"/subscriptions/{sub_id}",
        )
        return "removed"

    def delete_role_assignment(self, principal_id: str, role_id: str, scope: str) -> None:
        """Delete one role assignment, tolerating an already-absent one."""
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

    # ------------------------------------------------------------------
    # Helpers
    # ------------------------------------------------------------------

    def capture_tsv(self, cmd: list[str], *, allow_not_found: bool = False) -> str:
        """Run ``cmd`` and return stripped stdout; raise on failure.

        When ``allow_not_found`` is set, a not-found failure returns ``""`` instead
        of raising so callers can treat an absent resource as empty output.
        """
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
