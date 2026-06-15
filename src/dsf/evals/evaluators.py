"""Evaluators — pure-ish metric functions over a finished run (plan Task 8.1).

Each evaluator scores one quality dimension of a dry-run conveyor outcome on a
``[0.0, 1.0]`` scale, where ``1.0`` is perfect. They take already-loaded data
(the final :class:`Run` and, where relevant, the routed issues / proposals) so
they stay free of I/O and trivially unit-testable.

* :func:`groundedness`     — are routed issues backed only by real evidence?
* :func:`routing_accuracy` — did issues route to the expected product?
* :func:`verdict_match`    — did the run's filed/not-filed outcome match?
"""

from __future__ import annotations

from typing import TYPE_CHECKING

from dsf.contracts.enums import RunStatus

if TYPE_CHECKING:
    from dsf.contracts.models import Proposal, RoutedIssue, Run


def groundedness(
    run: Run,
    issues: list[RoutedIssue],
    proposals: list[Proposal] | None = None,
) -> float:
    """Fraction of routed issues whose proposal evidence is fully grounded.

    An issue is *grounded* when every ``evidence_id`` on its originating
    proposal corresponds to a real :class:`EvidenceItem` on ``run.evidence``
    (and the proposal carries at least one evidence id). Returns ``1.0`` when
    every issue is grounded — and ``1.0`` for the vacuous case of no issues
    (nothing ungrounded was filed).

    ``proposals`` may be passed explicitly; otherwise the proposal for each
    issue is not available and the issue is treated as ungrounded only when its
    proposal cannot be resolved. The runner always passes the loaded proposals.
    """
    if not issues:
        return 1.0

    known_evidence = {item.id for item in run.evidence}
    by_proposal_id: dict[str, Proposal] = {
        p.id: p for p in (proposals or [])
    }

    grounded = 0
    for issue in issues:
        proposal = by_proposal_id.get(issue.proposal_id)
        if proposal is None:
            # Cannot prove grounding without the proposal -> not grounded.
            continue
        evidence_ids = proposal.evidence_ids
        if evidence_ids and all(eid in known_evidence for eid in evidence_ids):
            grounded += 1

    return grounded / len(issues)


def routing_accuracy(issues: list[RoutedIssue], expected_product: str | None) -> float:
    """Fraction of routed issues that went to ``expected_product``.

    * If ``expected_product`` is ``None``, routing is *unconstrained*: any routed
      issue is acceptable, so the score is ``1.0`` whenever issues exist, and
      ``1.0`` when none were produced (nothing mis-routed).
    * Otherwise the score is the fraction of issues whose ``product`` equals
      ``expected_product``. With no issues produced (and a product expected) the
      score is ``0.0`` — the expectation went unmet.
    """
    if expected_product is None:
        return 1.0

    if not issues:
        return 0.0

    matched = sum(1 for issue in issues if issue.product == expected_product)
    return matched / len(issues)


def verdict_match(run: Run, expect_filed: bool) -> float:
    """``1.0`` when the run's filed-ness matches ``expect_filed`` else ``0.0``.

    A run is considered *filed* when its terminal status is
    :attr:`RunStatus.FILED`. A KILLED / ERROR / non-FILED terminal status counts
    as not filed.
    """
    filed = run.status == RunStatus.FILED
    return 1.0 if filed == expect_filed else 0.0


__all__ = ["groundedness", "routing_accuracy", "verdict_match"]
