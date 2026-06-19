"""Outcome policy tests - the deterministic maturity gate (validation-jury plan)."""

from __future__ import annotations

from dsf.contracts.enums import Verdict
from dsf.contracts.models import JurorVote, JuryResult
from dsf.council.outcome import decide_outcome

BAR = 0.67


def _jury(go: int, total: int) -> JuryResult:
    return JuryResult(votes=[JurorVote(juror=f"j{i}", go=(i < go)) for i in range(total)])


def test_supervised_unanimous_go_accepts():
    verdict, why = decide_outcome(_jury(3, 3), maturity="supervised", consensus_bar=BAR)
    assert verdict is Verdict.ACCEPT
    assert "proceed" in why.lower()


def test_supervised_split_go_escalates():
    verdict, _ = decide_outcome(_jury(2, 3), maturity="supervised", consensus_bar=BAR)
    assert verdict is Verdict.ESCALATE


def test_supervised_unanimous_against_kills():
    verdict, _ = decide_outcome(_jury(0, 3), maturity="supervised", consensus_bar=BAR)
    assert verdict is Verdict.KILL


def test_supervised_split_against_escalates():
    verdict, _ = decide_outcome(_jury(1, 3), maturity="supervised", consensus_bar=BAR)
    assert verdict is Verdict.ESCALATE


def test_shadow_unanimous_go_still_escalates():
    verdict, _ = decide_outcome(_jury(3, 3), maturity="shadow", consensus_bar=BAR)
    assert verdict is Verdict.ESCALATE


def test_shadow_unanimous_against_kills():
    verdict, _ = decide_outcome(_jury(0, 3), maturity="shadow", consensus_bar=BAR)
    assert verdict is Verdict.KILL


def test_autonomous_majority_go_accepts():
    verdict, _ = decide_outcome(_jury(2, 3), maturity="autonomous", consensus_bar=BAR)
    assert verdict is Verdict.ACCEPT


def test_autonomous_majority_against_kills():
    verdict, _ = decide_outcome(_jury(1, 3), maturity="autonomous", consensus_bar=BAR)
    assert verdict is Verdict.KILL


def test_autonomous_even_tie_escalates():
    verdict, _ = decide_outcome(_jury(1, 2), maturity="autonomous", consensus_bar=BAR)
    assert verdict is Verdict.ESCALATE


def test_empty_jury_escalates():
    verdict, _ = decide_outcome(JuryResult(), maturity="supervised", consensus_bar=BAR)
    assert verdict is Verdict.ESCALATE


def test_unknown_maturity_falls_back_to_supervised():
    verdict, _ = decide_outcome(_jury(3, 3), maturity="bogus", consensus_bar=BAR)
    assert verdict is Verdict.ACCEPT
