"""Shared teardown helpers for detecting already-absent (404) resources.

Both teardown paths — ``dsf delete``
(:class:`dsf.instance.deprovisioner.InstanceDeprovisioner`) and ``dsf offboard``
(:class:`dsf.instance.provisioner.InstanceOffboarder`) — must treat an
already-gone resource as success so a re-run can finish a partial teardown. They
share one not-found classifier here instead of each carrying its own copy.
"""

from __future__ import annotations

import subprocess

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
