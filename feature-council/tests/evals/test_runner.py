"""Tests for the eval suite, evaluators, and the CI gate (plan Task 8.1)."""

from __future__ import annotations

from dsf.evals.evaluators import groundedness, routing_accuracy, verdict_match, veto_accuracy
from dsf.evals.runner import (
    GATE_THRESHOLDS,
    METRIC_KEYS,
    gate,
    load_cases,
    main,
    run_case,
    run_suite,
)
from dsf_testing import build_test_services, config_with_product_record


def _services():
    return build_test_services(
        product="microbi",
        config=config_with_product_record("microbi", github_repo="joranbergfeld/microbi"),
    )


async def test_run_suite_returns_metrics_dict() -> None:
    """run_suite over the golden set yields the aggregate metrics dict with all four keys."""
    result = await run_suite(None, _services)

    assert set(result["metrics"]) == set(METRIC_KEYS)
    assert len(result["cases"]) == len(load_cases())
    # Every aggregate metric is a valid [0, 1] score.
    for key in METRIC_KEYS:
        assert 0.0 <= result["metrics"][key] <= 1.0


async def test_groundedness_is_perfect_on_golden() -> None:
    """The dry-run line only files grounded issues -> groundedness == 1.0."""
    result = await run_suite(None, _services)
    assert result["metrics"]["groundedness"] == 1.0


async def test_golden_set_has_at_least_five_cases() -> None:
    """The golden set must carry >= 5 cases (plan requirement)."""
    assert len(load_cases()) >= 5


async def test_killed_case_is_not_filed() -> None:
    """The all-agents-disabled / debounced case terminates not-FILED."""
    cases = {c["id"]: c for c in load_cases()}
    case = cases["all-agents-disabled-killed"]
    result = await run_case(case, _services)

    assert result["status"] != "FILED"
    # verdict_match rewards the matched not-filed expectation.
    assert result["metrics"]["verdict_match"] == 1.0


def test_main_gate_returns_zero_on_good_golden_set() -> None:
    """main(['--gate']) passes on the healthy golden set."""
    assert main(["--gate"], _services) == 0


def test_gate_returns_nonzero_on_injected_regression() -> None:
    """gate() over a fabricated low-metric suite result returns non-zero."""
    bad_result = {
        "cases": [],
        "metrics": {
            "groundedness": 0.10,
            "routing_accuracy": 0.20,
            "verdict_match": 0.30,
            "veto_accuracy": 0.40,
        },
    }
    assert gate(bad_result) != 0
    # All four metrics are below threshold -> four failures.
    assert gate(bad_result) == 4


def test_gate_returns_zero_on_passing_metrics() -> None:
    """gate() returns zero when every metric meets its threshold."""
    good = {
        "cases": [],
        "metrics": {key: 1.0 for key in METRIC_KEYS},
    }
    assert gate(good) == 0


def test_groundedness_evaluator_units() -> None:
    """groundedness: all ids known -> 1.0; an unknown id -> 0.0; no issues -> 1.0."""
    from dsf.contracts.enums import ProposalKind, SourceKind, TriggerKind
    from dsf.contracts.models import (
        EvidenceItem,
        Proposal,
        Provenance,
        RoutedIssue,
        Run,
    )

    ev = EvidenceItem(
        source_agent="sentry",
        claim="boom",
        raw_citation="http://x",
        provenance=Provenance(query_used="q", source_kind=SourceKind.SENTRY),
        product_hints=["microbi"],
    )
    run = Run(trigger=TriggerKind.SIGNAL, evidence=[ev])

    grounded_prop = Proposal(
        run_id=run.id,
        kind=ProposalKind.FIX,
        title="t",
        problem="p",
        proposed_change="c",
        product="microbi",
        evidence_ids=[ev.id],
    )
    ungrounded_prop = Proposal(
        run_id=run.id,
        kind=ProposalKind.FIX,
        title="t2",
        problem="p2",
        proposed_change="c2",
        product="microbi",
        evidence_ids=["does-not-exist"],
    )
    grounded_issue = RoutedIssue(
        proposal_id=grounded_prop.id,
        product="microbi",
        repo="r",
        title="t",
        body="b",
    )
    ungrounded_issue = RoutedIssue(
        proposal_id=ungrounded_prop.id,
        product="microbi",
        repo="r",
        title="t2",
        body="b2",
    )

    assert groundedness(run, [grounded_issue], [grounded_prop]) == 1.0
    assert groundedness(run, [ungrounded_issue], [ungrounded_prop]) == 0.0
    assert groundedness(run, [], []) == 1.0


def test_routing_accuracy_evaluator_units() -> None:
    """routing_accuracy: exact product, mismatch, None, and empty cases."""
    from dsf.contracts.models import RoutedIssue

    issue_a = RoutedIssue(proposal_id="1", product="microbi", repo="r", title="t", body="b")
    issue_b = RoutedIssue(
        proposal_id="2", product="other-product", repo="r", title="t", body="b"
    )

    assert routing_accuracy([issue_a], "microbi") == 1.0
    assert routing_accuracy([issue_a, issue_b], "microbi") == 0.5
    assert routing_accuracy([issue_a], "other-product") == 0.0
    # Unconstrained expectation.
    assert routing_accuracy([issue_a, issue_b], None) == 1.0
    # No issues, product expected -> unmet.
    assert routing_accuracy([], "microbi") == 0.0
    # No issues, nothing expected -> nothing mis-routed.
    assert routing_accuracy([], None) == 1.0


def test_verdict_match_evaluator_units() -> None:
    """verdict_match: FILED vs expectation in both directions."""
    from dsf.contracts.enums import RunStatus, TriggerKind
    from dsf.contracts.models import Run

    filed = Run(trigger=TriggerKind.SIGNAL, status=RunStatus.FILED)
    killed = Run(trigger=TriggerKind.SIGNAL, status=RunStatus.KILLED)

    assert verdict_match(filed, expect_filed=True) == 1.0
    assert verdict_match(filed, expect_filed=False) == 0.0
    assert verdict_match(killed, expect_filed=False) == 1.0
    assert verdict_match(killed, expect_filed=True) == 0.0


def test_gate_thresholds_match_spec() -> None:
    """Gate thresholds are the spec values."""
    assert GATE_THRESHOLDS == {
        "groundedness": 0.99,
        "routing_accuracy": 0.95,
        "verdict_match": 0.85,
        "veto_accuracy": 0.95,
    }


def test_veto_accuracy_evaluator_units() -> None:
    """veto_accuracy: correct behavior in all four combinations."""
    from dsf.contracts.enums import Verdict
    from dsf.contracts.models import CouncilVerdict, CriticScore

    # Build a KILL verdict (from_scores with no scores -> weighted_score 0.0 < 0.6)
    kill_v = CouncilVerdict.from_scores("p1", [], threshold=0.6)
    assert kill_v.verdict == Verdict.KILL

    # Build an ACCEPT verdict (high score clears threshold)
    high_score = CriticScore(critic="value", score=1.0)
    accept_v = CouncilVerdict.from_scores("p2", [high_score], threshold=0.6)
    assert accept_v.verdict == Verdict.ACCEPT

    # must_veto=False -> always 1.0
    assert veto_accuracy([], must_veto=False) == 1.0
    assert veto_accuracy([accept_v], must_veto=False) == 1.0

    # must_veto=True, no verdicts -> 0.0
    assert veto_accuracy([], must_veto=True) == 0.0
    # must_veto=True, only ACCEPT -> 0.0 (no veto fired)
    assert veto_accuracy([accept_v], must_veto=True) == 0.0
    # must_veto=True, at least one KILL -> 1.0
    assert veto_accuracy([kill_v], must_veto=True) == 1.0
    assert veto_accuracy([accept_v, kill_v], must_veto=True) == 1.0


async def test_adversarial_cases_exist_in_golden_set() -> None:
    """All four adversarial cases are present in the golden set."""
    adversarial_ids = {
        "adversarial-debounce-burst",
        "adversarial-ungrounded-proposal",
        "adversarial-routing-word-boundary",
        "adversarial-duplicate-veto",
    }
    case_ids = {c["id"] for c in load_cases()}
    assert adversarial_ids <= case_ids


async def test_adversarial_debounce_burst_is_killed() -> None:
    """Debounce burst case terminates KILLED (not FILED)."""
    cases = {c["id"]: c for c in load_cases()}
    result = await run_case(cases["adversarial-debounce-burst"], _services)
    assert result["status"] == "KILLED"
    assert result["metrics"]["verdict_match"] == 1.0


async def test_adversarial_ungrounded_proposal_is_grounded() -> None:
    """Ungrounded-proposal case: S4 kills the fake proposal -> groundedness 1.0."""
    cases = {c["id"]: c for c in load_cases()}
    result = await run_case(cases["adversarial-ungrounded-proposal"], _services)
    assert result["status"] == "FILED"
    assert result["metrics"]["groundedness"] == 1.0


async def test_adversarial_routing_word_boundary_routes_correctly() -> None:
    """Word-boundary routing case: compound hint routes to expected product."""
    cases = {c["id"]: c for c in load_cases()}
    result = await run_case(cases["adversarial-routing-word-boundary"], _services)
    assert result["status"] == "FILED"
    assert result["metrics"]["routing_accuracy"] == 1.0


async def test_adversarial_duplicate_veto_vetoes_proposal() -> None:
    """Duplicate veto case: council vetoes the proposal -> veto_accuracy 1.0."""
    cases = {c["id"]: c for c in load_cases()}
    result = await run_case(cases["adversarial-duplicate-veto"], _services)
    assert result["status"] == "FILED"
    assert result["metrics"]["veto_accuracy"] == 1.0
