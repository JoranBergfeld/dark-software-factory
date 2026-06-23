"""Squad governance: map the per-product maturity dial to repo settings.

Low maturity disables auto-merge (a human merges every squad PR); high maturity
enables it (a PR that passes required CI merges on its own). The dial governs the
repo capability, not Ralph's behaviour: Ralph always opens PRs, and this decides
what may happen to them. Applied at provisioning and re-applied whenever the dial
changes while the factory runs.
"""

from __future__ import annotations

from dsf.instance.spec import InstanceSpec


def governance_commands(spec: InstanceSpec) -> list[list[str]]:
    """Return the ``gh`` commands that apply ``spec.creation_maturity`` to the repo."""
    enabled = "true" if spec.creation_maturity == "high" else "false"
    return [
        [
            "gh", "api", "--method", "PATCH", f"repos/{spec.github_repo()}",
            "-F", f"allow_auto_merge={enabled}",
        ]
    ]
