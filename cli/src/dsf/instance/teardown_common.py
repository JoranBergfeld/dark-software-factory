"""Shared teardown helpers for detecting already-absent (404) resources and for
refusing to delete resource groups DSF did not create.

Both teardown paths — ``dsf delete``
(:class:`dsf.instance.deprovisioner.InstanceDeprovisioner`) and ``dsf offboard``
(:class:`dsf.instance.provisioner.InstanceOffboarder`) — must treat an
already-gone resource as success so a re-run can finish a partial teardown, and
must never delete a resource group that is not tagged ``managed-by=dsf`` (which
would risk deleting a foreign group that merely shares a name). They share one
not-found classifier and one tag-guarded delete here instead of each carrying a
copy.
"""

from __future__ import annotations

import json
import subprocess
from collections.abc import Callable
from typing import Any

from dsf.instance.tagging import MANAGED_BY_TAG, MANAGED_BY_VALUE

Runner = Callable[..., Any]

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