"""The single ``creation:ready`` bootstrap issue for greenfield seeding.

``dsf charter implement`` files this one issue to hand a greenfield product's
accepted charter to the Copilot Coding Agent, which runs the whole Spec Kit
lifecycle in one session. The charter is embedded as UNTRUSTED data (ADR 0017)
through the shared :func:`dsf.charter.context.charter_context` chokepoint.
"""

from __future__ import annotations

from dsf.charter.constitution import CONSTITUTION_PATH
from dsf.charter.context import charter_context
from dsf.charter.sync import CHARTER_PATH
from dsf.contracts.charter import Charter

#: Requested (not guaranteed) model for the build — Copilot's model is a
#: repo/account setting, so the issue states this as a request. See the design's
#: Risks section.
_REQUESTED_MODEL = "Claude Opus 4.8"


def render_bootstrap_issue(charter: Charter) -> tuple[str, str]:
    """Return ``(title, body)`` for the greenfield Spec Kit bootstrap issue."""
    title = f"Build {charter.product} from its charter (Spec Kit)"
    body = (
        f"Bootstrap the **{charter.product}** product from its accepted charter "
        "using the GitHub Spec Kit lifecycle, in a single session.\n\n"
        "## What to do (one session)\n"
        f"1. `/speckit.specify` — derive the product specification from the charter "
        f"below and `{CHARTER_PATH}`.\n"
        "2. `/speckit.plan` — choose a sensible tech stack and architecture "
        "(a paved-road default is not wired yet — your choice for now).\n"
        "3. `/speckit.tasks` — break the plan into actionable tasks.\n"
        "4. Implement the tasks and open pull request(s); keep the `ci` check green.\n\n"
        "## Governing documents\n"
        f"- Constitution: `{CONSTITUTION_PATH}` (derived from the charter — your "
        "principles and quality gates).\n"
        f"- Charter: `{CHARTER_PATH}` (the human-owned source of truth).\n\n"
        "## Product charter (reference)\n"
        f"{charter_context(charter)}\n\n"
        "---\n"
        f"_Model request: this build is intended to run as {_REQUESTED_MODEL}. "
        "Copilot's model is a repository/account setting, so treat this as a "
        "request, not a guarantee._"
    )
    return title, body


__all__ = ["render_bootstrap_issue"]
