"""Branch-protection ruleset: map the creation-maturity dial to a real ruleset.

This replaces the old ``allow_auto_merge``-only no-op (issue #54). The ruleset is
applied with the operator's interactive ``gh`` auth at provision time (the operator
is admin on the just-created repo), so the DSF App needs no
``administration:write`` scope.

The dial governs repo controls, not any agent's behaviour:

- ``low``  — require 1 human approval **and** the green ``ci`` check; auto-merge off.
- ``high`` — require 0 reviews but still the green ``ci`` check; repo auto-merge on,
  so a PR merges itself once ``ci`` is green (no human).

``ci`` is a DSF convention: the product CI must publish a status check named ``ci``
for merges (low) / auto-merge (high) to proceed.
"""

from __future__ import annotations

from dsf.instance.spec import InstanceSpec

RULESET_NAME = "dsf-creation"
_REQUIRED_CHECK_CONTEXT = "ci"


def ruleset_payload(spec: InstanceSpec) -> dict:
    """Build the repo ruleset body for ``spec.creation_maturity``."""
    reviews = 0 if spec.creation_maturity == "high" else 1
    return {
        "name": RULESET_NAME,
        "target": "branch",
        "enforcement": "active",
        "conditions": {"ref_name": {"include": ["~DEFAULT_BRANCH"], "exclude": []}},
        "rules": [
            {
                "type": "pull_request",
                "parameters": {
                    "required_approving_review_count": reviews,
                    "dismiss_stale_reviews_on_push": True,
                    "require_code_owner_review": False,
                    "require_last_push_approval": False,
                    "required_review_thread_resolution": False,
                },
            },
            {
                "type": "required_status_checks",
                "parameters": {
                    "strict_required_status_checks_policy": True,
                    "required_status_checks": [{"context": _REQUIRED_CHECK_CONTEXT}],
                },
            },
        ],
    }


def auto_merge_command(spec: InstanceSpec) -> list[str]:
    """Return the ``gh`` command toggling repo auto-merge for the dial."""
    enabled = "true" if spec.creation_maturity == "high" else "false"
    return [
        "gh", "api", "--method", "PATCH", f"repos/{spec.github_repo()}",
        "-F", f"allow_auto_merge={enabled}",
    ]
