"""PR Feedback Watcher — event-driven capture of human verdicts on spec PRs.

This runs outside the per-run conveyor (design §6.4). A GitHub PR webhook lands
here; we map it to a :class:`PrOutcome` (the human verdict + proposed-vs-final
spec diff + triage actions), then write the learning-loop write path: a durable
long-term record AND a product-scoped Lesson retrievable next run.
"""

from __future__ import annotations

import re
from typing import TYPE_CHECKING, Literal

from pydantic import BaseModel, Field

from dsf.learning.lessons import outcome_to_lesson

if TYPE_CHECKING:
    from dsf.container import Services

Verdict = Literal["approved", "rejected", "changes_requested"]

#: Marker patterns we plant in the PR body to link a PR back to its run.
_PRODUCT_RE = re.compile(r"(?im)^\s*(?:dsf-)?product\s*[:=]\s*(?P<product>[\w./-]+)\s*$")
_PROPOSAL_RE = re.compile(r"(?im)^\s*(?:dsf-)?proposal\s*[:=]\s*(?P<proposal>[\w./-]+)\s*$")
#: Label convention: ``product:<key>`` ties an issue/PR to a product.
_PRODUCT_LABEL_RE = re.compile(r"(?i)^product[:/](?P<product>[\w./-]+)$")


class PrOutcome(BaseModel):
    """The captured outcome of a spec PR (the learning-loop input record)."""

    id: str
    product: str
    issue_url: str | None = None
    proposal_title: str
    verdict: Verdict
    spec_diff: str = ""
    rationale: str = ""
    labels_changed: list[str] = Field(default_factory=list)


def _label_names(labels: object) -> list[str]:
    """Normalize GitHub label entries (dicts or strings) to name strings."""
    names: list[str] = []
    if isinstance(labels, list):
        for label in labels:
            if isinstance(label, dict):
                name = label.get("name")
                if name:
                    names.append(str(name))
            elif label:
                names.append(str(label))
    return names


def _product_from_labels(labels: list[str]) -> str | None:
    """Derive the product key from a ``product:<key>`` label, if present."""
    for name in labels:
        match = _PRODUCT_LABEL_RE.match(name.strip())
        if match:
            return match.group("product")
    return None


def _derive_verdict(event: dict, pull_request: dict) -> Verdict:
    """Map webhook action/merged/review state to a verdict.

    * merged PR -> ``approved``
    * closed-but-not-merged -> ``rejected``
    * a ``changes_requested`` review -> ``changes_requested``
    * anything else defaults to ``changes_requested`` (a soft, non-terminal state)
    """
    action = str(event.get("action", "")).lower()

    review = event.get("review")
    if isinstance(review, dict):
        state = str(review.get("state", "")).lower()
        if state == "changes_requested":
            return "changes_requested"
        if state == "approved":
            return "approved"

    review_state = str(event.get("review_state", "")).lower()
    if review_state == "changes_requested":
        return "changes_requested"

    if action == "closed" or pull_request.get("state") == "closed":
        return "approved" if pull_request.get("merged") else "rejected"

    return "changes_requested"


def parse_pr_event(event: dict) -> PrOutcome:
    """Map a GitHub PR webhook-shaped dict to a :class:`PrOutcome`.

    Tolerates missing fields with sensible defaults. Product/proposal linkage is
    derived from our planted markers in the PR body, falling back to a
    ``product:<key>`` label, then ``"unknown"``.
    """
    pull_request = event.get("pull_request")
    if not isinstance(pull_request, dict):
        pull_request = {}

    body = str(pull_request.get("body") or "")
    labels = _label_names(pull_request.get("labels"))
    labels_changed = _label_names(event.get("labels_changed")) or _label_names(
        event.get("labels")
    )

    product_match = _PRODUCT_RE.search(body)
    product = (
        product_match.group("product")
        if product_match
        else _product_from_labels(labels) or "unknown"
    )

    proposal_match = _PROPOSAL_RE.search(body)
    proposal_id = proposal_match.group("proposal") if proposal_match else None
    pr_id = str(
        pull_request.get("id")
        or pull_request.get("number")
        or proposal_id
        or "unknown"
    )

    title = str(pull_request.get("title") or "untitled proposal")

    issue = event.get("issue")
    issue_url = None
    if isinstance(issue, dict):
        issue_url = issue.get("html_url") or issue.get("url")
    issue_url = issue_url or pull_request.get("issue_url") or event.get("issue_url")

    spec_diff = str(
        event.get("spec_diff")
        or pull_request.get("spec_diff")
        or pull_request.get("diff")
        or ""
    )
    rationale = str(
        event.get("rationale")
        or _review_body(event)
        or pull_request.get("rationale")
        or ""
    )

    verdict = _derive_verdict(event, pull_request)

    return PrOutcome(
        id=pr_id,
        product=product,
        issue_url=str(issue_url) if issue_url else None,
        proposal_title=title,
        verdict=verdict,
        spec_diff=spec_diff,
        rationale=rationale,
        labels_changed=labels_changed,
    )


def _review_body(event: dict) -> str:
    """Pull a human rationale from an attached review body, if any."""
    review = event.get("review")
    if isinstance(review, dict):
        return str(review.get("body") or "")
    return ""


async def record_outcome(outcome: PrOutcome, services: Services) -> None:
    """Write the learning-loop write path for one PR outcome.

    Persists a durable long-term record (for calibration/retrieval) AND a
    product-scoped Lesson (retrievable via ``MemoryStore.get_lessons``).
    """
    record = {
        "kind": "pr_outcome",
        "pr_id": outcome.id,
        "product": outcome.product,
        "issue_url": outcome.issue_url,
        "verdict": outcome.verdict,
        "proposal_title": outcome.proposal_title,
        "spec_diff": outcome.spec_diff,
        "rationale": outcome.rationale,
        "labels_changed": list(outcome.labels_changed),
        "text": (
            f"product={outcome.product} verdict={outcome.verdict} "
            f"{outcome.proposal_title} {outcome.rationale}"
        ),
    }
    await services.memory.put_record(record)

    lesson = outcome_to_lesson(outcome)
    await services.memory.put_lesson(dict(lesson))


async def handle_pr_event(event: dict, services: Services) -> PrOutcome:
    """Parse a PR webhook event and record its outcome; return the outcome."""
    outcome = parse_pr_event(event)
    await record_outcome(outcome, services)
    return outcome


__all__ = [
    "PrOutcome",
    "handle_pr_event",
    "parse_pr_event",
    "record_outcome",
]
