# Feature Council Validation Jury Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Layer a separate, model-diverse validation jury and a deterministic, maturity-gated outcome policy over the existing critic recommendation, so the council can ACCEPT, ESCALATE to a human, or KILL a proposal.

**Architecture:** The enabled critics keep producing a deterministic *recommendation* (the proposer tier). A new *validation jury* of distinct model personas then reviews that recommendation; offline it echoes the recommendation so the line stays green with no LLM. A new deterministic *outcome policy* maps the jury panel plus the product's *maturity dial* onto the final verdict. Verdict gains a third outcome, `ESCALATE`, and S5 routes escalations to a human review queue instead of onward to routing.

**Tech Stack:** Python 3.12, Pydantic v2, pytest (`asyncio_mode = auto`), uv workspace (`core` + `feature-council` members sharing the `dsf.*` namespace), import-linter boundaries (core may not import app members).

---

## Background the engineer needs

This is the first of three slices of the deliberative-council redesign recorded in ADR 0011 (`docs/adr/0011-feature-council-deliberative-redesign.md`) and the design spec `docs/superpowers/specs/2026-06-19-feature-council-deliberative-redesign-design.md`. This slice (Plan 1) delivers the governance novelty: the judge is separated from the proposer, a model-diverse panel reduces single-judge bias, and a maturity dial gates how much autonomy the line has before a human is asked. Plan 2 (deliberation council) and Plan 3 (governed pull intake) are out of scope here.

Key invariants you must not break:

1. **The whole line runs offline and deterministically with NO model handlers registered.** The model port (`DeterministicModelClient`) returns an echo prefixed with `ECHO_PREFIX` when no handler matches a prompt tag, so load-bearing logic must have a deterministic fallback. The jury's fallback is: each juror votes the recommendation's own verdict (GO when the recommendation is ACCEPT, NO-GO when it is KILL). Offline, the panel is therefore unanimous and mirrors the recommendation exactly.
2. **Default maturity is `supervised`, which acts only on a unanimous jury.** Offline the jury is unanimous, so an ACCEPT recommendation still proceeds and a KILL recommendation still dies. This keeps the end-to-end dry-run line (`feature-council/tests/e2e/test_dry_run_line.py`) green without modification.
3. **`core` may not import any app member.** Because `CouncilVerdict` (in `core`) will carry an optional `jury` field, the jury contracts `JurorVote` and `JuryResult` must live in `core/src/dsf/contracts/models.py`. The `Maturity` enum is a council policy concept and stays in `feature-council`.

### The maturity ladder (the deterministic gate)

| Maturity | Behavior |
| --- | --- |
| `shadow` | Humans decide everything. Any proceed escalates; only a unanimous NO-GO kills. |
| `supervised` (default) | Act only on a strong (near-unanimous) jury: unanimous GO accepts, unanimous NO-GO kills, anything contested escalates. |
| `autonomous` | Jury majority rules: majority GO accepts, majority NO-GO kills, an even tie escalates. |

`supervised` uses `consensus_bar` (default `0.67`). For a 3-juror panel, `3-0` gives consensus `1.0` (>= bar, acts) and `2-1` gives consensus `0.667` (< bar, escalates), so `supervised` effectively requires unanimity. `autonomous` ignores the bar and follows the majority. `shadow` ignores the bar and always escalates a proceed.

---

## File Structure

**Create:**
- `feature-council/src/dsf/council/outcome.py` - `Maturity` enum + `decide_outcome(jury, *, maturity, consensus_bar) -> tuple[Verdict, str]`, the pure deterministic gate.
- `feature-council/src/dsf/council/jury.py` - `convene_jury(recommendation, proposal, run, services) -> JuryResult`, the model-diverse panel with deterministic fallback.
- `feature-council/tests/council/test_outcome.py` - outcome policy unit tests.
- `feature-council/tests/council/test_jury.py` - jury unit tests.
- `feature-council/tests/orchestrator/test_s5_escalation.py` - S5 review-queue routing test.

**Modify:**
- `core/src/dsf/contracts/enums.py` - add `Verdict.ESCALATE`.
- `core/src/dsf/contracts/models.py` - add `JurorVote`, `JuryResult`; add optional `jury` field to `CouncilVerdict`.
- `core/src/dsf/contracts/__init__.py` - export `JurorVote`, `JuryResult`.
- `core/src/dsf/contracts/schemas/CouncilVerdict.json` - regenerated snapshot.
- `core/src/dsf/config/flags.py` - add `maturity_level`, `consensus_bar`, `jury_roster` accessors.
- `config/defaults.json` - add `default_maturity`, `default_consensus_bar`, `jury.roster`.
- `feature-council/src/dsf/council/decision.py` - rewire `decide()`: recommendation -> jury -> outcome policy.
- `feature-council/src/dsf/orchestrator/stations/s5_council.py` - handle the `ESCALATE` outcome (review queue, not routed).
- `core/src/dsf/memory/consolidation.py` - label `ESCALATE` runs `"escalated"`.
- `core/tests/contracts/test_models.py`, `core/tests/config/test_flags.py`, `core/tests/memory/test_memory.py`, `feature-council/tests/council/test_decision.py` - extend tests.

**Validation commands (run from repo root):**
- Targeted: `uv run pytest <path>::<test> -v`
- Full suite: `uv run pytest -q`
- Lint: `uv run ruff check .`
- Eval gate: `uv run python -m dsf.evals.runner --gate`

---

## Task 1: Add the ESCALATE verdict

**Files:**
- Modify: `core/src/dsf/contracts/enums.py:48-52`
- Test: `core/tests/contracts/test_models.py`

- [x] **Step 1: Write the failing test**

Add to `core/tests/contracts/test_models.py` (the `Verdict` import already exists at the top of the file):

```python
def test_verdict_has_escalate_outcome():
    assert Verdict.ESCALATE.value == "ESCALATE"
    assert Verdict.ESCALATE not in (Verdict.ACCEPT, Verdict.KILL)
```

- [x] **Step 2: Run test to verify it fails**

Run: `uv run pytest core/tests/contracts/test_models.py::test_verdict_has_escalate_outcome -v`
Expected: FAIL with `AttributeError: ESCALATE`.

- [x] **Step 3: Add the enum member**

In `core/src/dsf/contracts/enums.py`, change the `Verdict` class:

```python
class Verdict(StrEnum):
    """Council verdict outcome."""

    ACCEPT = "ACCEPT"
    ESCALATE = "ESCALATE"
    KILL = "KILL"
```

- [x] **Step 4: Run test to verify it passes**

Run: `uv run pytest core/tests/contracts/test_models.py::test_verdict_has_escalate_outcome -v`
Expected: PASS.

- [x] **Step 5: Commit**

```bash
git add core/src/dsf/contracts/enums.py core/tests/contracts/test_models.py
git commit -m "feat(contracts): add ESCALATE verdict outcome"
```

---

## Task 2: Add the jury contracts

**Files:**
- Modify: `core/src/dsf/contracts/models.py` (after `CriticScore`, before `CouncilVerdict`)
- Modify: `core/src/dsf/contracts/__init__.py`
- Test: `core/tests/contracts/test_models.py`

- [x] **Step 1: Write the failing test**

Add to `core/tests/contracts/test_models.py`. First extend the existing `from dsf.contracts.models import (...)` block (currently `CouncilVerdict, CriticScore, EvidenceItem, Proposal, Provenance, Run`) to:

```python
from dsf.contracts.models import (
    CouncilVerdict,
    CriticScore,
    EvidenceItem,
    JurorVote,
    JuryResult,
    Proposal,
    Provenance,
    Run,
)
```

Then add:

```python
def test_jury_result_reports_fraction_consensus_majority():
    jr = JuryResult(
        votes=[
            JurorVote(juror="a", go=True),
            JurorVote(juror="b", go=True),
            JurorVote(juror="c", go=False),
        ]
    )
    assert abs(jr.go_fraction - 2 / 3) < 1e-9
    assert jr.majority_go is True
    assert abs(jr.consensus - 2 / 3) < 1e-9


def test_jury_result_empty_has_no_consensus():
    jr = JuryResult()
    assert jr.go_fraction == 0.0
    assert jr.consensus == 0.0
    assert jr.majority_go is False
```

- [x] **Step 2: Run test to verify it fails**

Run: `uv run pytest core/tests/contracts/test_models.py::test_jury_result_reports_fraction_consensus_majority -v`
Expected: FAIL with `ImportError` (cannot import `JurorVote`).

- [x] **Step 3: Add the contracts**

In `core/src/dsf/contracts/models.py`, insert immediately after the `CriticScore` class (after its `rationale: str = ""` line) and before `class CouncilVerdict`:

```python
class JurorVote(BaseModel):
    """One juror's go / no-go vote validating a council recommendation."""

    juror: str
    go: bool
    rationale: str = ""


class JuryResult(BaseModel):
    """The validation jury's panel of votes over a recommendation."""

    votes: list[JurorVote] = Field(default_factory=list)

    @property
    def go_fraction(self) -> float:
        """Fraction of jurors voting to proceed (0.0 when no votes)."""
        if not self.votes:
            return 0.0
        return sum(1 for v in self.votes if v.go) / len(self.votes)

    @property
    def majority_go(self) -> bool:
        """Whether a strict majority voted to proceed."""
        return self.go_fraction > 0.5

    @property
    def consensus(self) -> float:
        """Agreement strength of the majority side (1.0 = unanimous)."""
        if not self.votes:
            return 0.0
        go = self.go_fraction
        return max(go, 1.0 - go)
```

`BaseModel` and `Field` are already imported at the top of `models.py`.

- [x] **Step 4: Export the contracts**

In `core/src/dsf/contracts/__init__.py`, add `JurorVote` and `JuryResult` to both the `from dsf.contracts.models import (...)` block and the `__all__` list (keep the existing alphabetical-ish grouping):

```python
from dsf.contracts.models import (
    AuditRecord,
    CouncilVerdict,
    CriticScore,
    EvidenceItem,
    JurorVote,
    JuryResult,
    Proposal,
    Provenance,
    RoutedIssue,
    Run,
)
```

```python
    "CouncilVerdict",
    "CriticScore",
    "EvidenceItem",
    "JurorVote",
    "JuryResult",
    "Proposal",
```

- [x] **Step 5: Run tests to verify they pass**

Run: `uv run pytest core/tests/contracts/test_models.py -v`
Expected: PASS (new tests plus all existing ones).

- [x] **Step 6: Commit**

```bash
git add core/src/dsf/contracts/models.py core/src/dsf/contracts/__init__.py core/tests/contracts/test_models.py
git commit -m "feat(contracts): add JurorVote and JuryResult"
```

---

## Task 3: Carry an optional jury on CouncilVerdict

**Files:**
- Modify: `core/src/dsf/contracts/models.py` (the `CouncilVerdict` field block, around line 123)
- Modify: `core/src/dsf/contracts/schemas/CouncilVerdict.json` (regenerated)
- Test: `core/tests/contracts/test_models.py`

- [x] **Step 1: Write the failing test**

Add to `core/tests/contracts/test_models.py`:

```python
def test_council_verdict_carries_optional_jury():
    jr = JuryResult(votes=[JurorVote(juror="a", go=True)])
    v = CouncilVerdict(
        proposal_id="p",
        verdict=Verdict.ESCALATE,
        weighted_score=0.7,
        threshold=0.6,
        jury=jr,
    )
    assert v.jury is not None
    assert v.jury.votes[0].juror == "a"


def test_council_verdict_jury_defaults_to_none():
    base = CouncilVerdict.from_scores("p", [], threshold=0.6)
    assert base.jury is None
```

- [x] **Step 2: Run test to verify it fails**

Run: `uv run pytest core/tests/contracts/test_models.py::test_council_verdict_carries_optional_jury -v`
Expected: FAIL with a Pydantic validation error (unexpected keyword `jury`).

- [x] **Step 3: Add the field**

In `core/src/dsf/contracts/models.py`, add the `jury` field to `CouncilVerdict` immediately after the `scores` field:

```python
    scores: list[CriticScore] = Field(default_factory=list)
    jury: JuryResult | None = None
    rationale: str = ""
```

`from_scores` does not set `jury`, so it stays `None` there - no change needed to the classmethod.

- [x] **Step 4: Run test to verify it passes**

Run: `uv run pytest core/tests/contracts/test_models.py -v`
Expected: PASS.

- [x] **Step 5: Regenerate the schema snapshot**

The committed JSON Schemas under `core/src/dsf/contracts/schemas/` are generated. Refresh them so the repo stays consistent:

Run: `uv run python -m dsf.contracts.export_schema`
Expected: prints `wrote .../CouncilVerdict.json` (and the other models). Only `CouncilVerdict.json` changes (it gains `jury` plus nested `JurorVote`/`JuryResult` `$defs`).

- [x] **Step 6: Run the contracts test that exercises export**

Run: `uv run pytest core/tests/contracts/test_models.py::test_export_schemas -v`
Expected: PASS.

- [x] **Step 7: Commit**

```bash
git add core/src/dsf/contracts/models.py core/src/dsf/contracts/schemas/CouncilVerdict.json core/tests/contracts/test_models.py
git commit -m "feat(contracts): carry optional validation jury on CouncilVerdict"
```

---

## Task 4: The maturity-gated outcome policy

**Files:**
- Create: `feature-council/src/dsf/council/outcome.py`
- Test: `feature-council/tests/council/test_outcome.py`

- [x] **Step 1: Write the failing test**

Create `feature-council/tests/council/test_outcome.py`:

```python
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
```

- [x] **Step 2: Run test to verify it fails**

Run: `uv run pytest feature-council/tests/council/test_outcome.py -v`
Expected: FAIL with `ModuleNotFoundError: dsf.council.outcome`.

- [x] **Step 3: Write the implementation**

Create `feature-council/src/dsf/council/outcome.py`:

```python
"""Deterministic, maturity-gated outcome policy for the council.

Maps a validation :class:`JuryResult` plus the product's maturity dial onto the
final :class:`Verdict` (ACCEPT / ESCALATE / KILL). The jury supplies judgment;
the maturity dial decides how much autonomy the line has before a human is asked.
Pure function: no I/O, fully deterministic.
"""

from __future__ import annotations

from enum import StrEnum

from dsf.contracts.enums import Verdict
from dsf.contracts.models import JuryResult


class Maturity(StrEnum):
    """How much autonomy the line has before a human is consulted."""

    SHADOW = "shadow"          # advise only: humans decide everything
    SUPERVISED = "supervised"  # act only on a near-unanimous jury, else escalate
    AUTONOMOUS = "autonomous"  # act on a jury majority; escalate only on a tie


def _coerce(maturity: str) -> Maturity:
    try:
        return Maturity(maturity)
    except ValueError:
        return Maturity.SUPERVISED


def decide_outcome(
    jury: JuryResult,
    *,
    maturity: str,
    consensus_bar: float,
) -> tuple[Verdict, str]:
    """Resolve the final verdict from the jury panel and the maturity dial."""
    level = _coerce(maturity)
    if not jury.votes:
        return Verdict.ESCALATE, "No jurors voted; escalating to a human."

    go = jury.go_fraction
    consensus = jury.consensus
    majority_go = jury.majority_go

    if level is Maturity.SHADOW:
        if not majority_go and consensus >= 1.0:
            return Verdict.KILL, (
                f"shadow maturity: jury unanimously against (go={go:.2f}); killed."
            )
        return Verdict.ESCALATE, (
            f"shadow maturity: routing to a human for the final call (go={go:.2f})."
        )

    if level is Maturity.AUTONOMOUS:
        if go == 0.5:
            return Verdict.ESCALATE, (
                f"autonomous maturity: jury split evenly (go={go:.2f}); escalating."
            )
        if majority_go:
            return Verdict.ACCEPT, (
                f"autonomous maturity: jury majority in favor (go={go:.2f}); proceeding."
            )
        return Verdict.KILL, (
            f"autonomous maturity: jury majority against (go={go:.2f}); killed."
        )

    # Maturity.SUPERVISED (default): act only on a strong (near-unanimous) jury.
    strong = consensus >= consensus_bar
    if strong and majority_go:
        return Verdict.ACCEPT, (
            f"supervised maturity: strong jury consensus to proceed "
            f"(go={go:.2f}, consensus={consensus:.2f} >= bar {consensus_bar:.2f})."
        )
    if strong and not majority_go:
        return Verdict.KILL, (
            f"supervised maturity: strong jury consensus against "
            f"(go={go:.2f}, consensus={consensus:.2f} >= bar {consensus_bar:.2f}); killed."
        )
    return Verdict.ESCALATE, (
        f"supervised maturity: jury split (consensus={consensus:.2f} < bar "
        f"{consensus_bar:.2f}); escalating to a human."
    )


__all__ = ["Maturity", "decide_outcome"]
```

- [x] **Step 4: Run test to verify it passes**

Run: `uv run pytest feature-council/tests/council/test_outcome.py -v`
Expected: PASS (all 11 tests).

- [x] **Step 5: Commit**

```bash
git add feature-council/src/dsf/council/outcome.py feature-council/tests/council/test_outcome.py
git commit -m "feat(council): add maturity-gated outcome policy"
```

---

## Task 5: Config accessors for maturity, consensus bar, and jury roster

**Files:**
- Modify: `core/src/dsf/config/flags.py`
- Modify: `config/defaults.json`
- Test: `core/tests/config/test_flags.py`

- [x] **Step 1: Write the failing test**

Add to `core/tests/config/test_flags.py`. First extend the existing `from dsf.config.flags import (...)` block (currently `DEFAULT_THRESHOLD, agent_enabled, critic_enabled, dry_run_global, threshold, triggers_paused, weights`) to also import the three new accessors:

```python
from dsf.config.flags import (
    DEFAULT_THRESHOLD,
    agent_enabled,
    consensus_bar,
    critic_enabled,
    dry_run_global,
    jury_roster,
    maturity_level,
    threshold,
    triggers_paused,
    weights,
)
```

`InMemoryConfigStore` is already imported in this file. Then add:

```python
def test_maturity_defaults_to_supervised():
    cfg = InMemoryConfigStore.from_defaults()
    assert maturity_level(cfg) == "supervised"


def test_maturity_per_product_override():
    cfg = InMemoryConfigStore(
        {"default_maturity": "supervised", "maturity": {"acme": "autonomous"}}
    )
    assert maturity_level(cfg, product="acme") == "autonomous"
    assert maturity_level(cfg, product="other") == "supervised"


def test_consensus_bar_default():
    cfg = InMemoryConfigStore.from_defaults()
    assert consensus_bar(cfg) == 0.67


def test_consensus_bar_per_product_override():
    cfg = InMemoryConfigStore(
        {"default_consensus_bar": 0.67, "consensus_bar": {"acme": 0.9}}
    )
    assert consensus_bar(cfg, product="acme") == 0.9
    assert consensus_bar(cfg, product="other") == 0.67


def test_jury_roster_default():
    cfg = InMemoryConfigStore.from_defaults()
    assert jury_roster(cfg) == ["pragmatist", "skeptic", "user_advocate"]
```

- [x] **Step 2: Run test to verify it fails**

Run: `uv run pytest core/tests/config/test_flags.py::test_maturity_defaults_to_supervised -v`
Expected: FAIL with `ImportError` (cannot import `maturity_level`).

- [x] **Step 3: Add the accessors**

In `core/src/dsf/config/flags.py`, add the constants after `DEFAULT_WEIGHT` (line 28):

```python
#: Fallback config key for the per-product maturity dial.
DEFAULT_MATURITY_KEY = "default_maturity"
#: Hard fallback when ``default_maturity`` is itself unset.
DEFAULT_MATURITY = "supervised"
#: Fallback config key for the per-product jury consensus bar.
DEFAULT_CONSENSUS_BAR_KEY = "default_consensus_bar"
#: Hard fallback when ``default_consensus_bar`` is itself unset.
DEFAULT_CONSENSUS_BAR = 0.67
#: Config key for the jury roster (list of juror persona names).
JURY_ROSTER_KEY = "jury.roster"
#: Hard fallback roster when no ``jury.roster`` is configured.
DEFAULT_JURY_ROSTER = ("pragmatist", "skeptic", "user_advocate")
```

Add the three accessor functions after `weights` (line 74), before `__all__`:

```python
def maturity_level(cfg: ConfigStore, product: str | None = None) -> str:
    """Per-product maturity dial, falling back to ``default_maturity``.

    Resolution order: ``maturity.<product>`` -> ``default_maturity`` ->
    :data:`DEFAULT_MATURITY`.
    """
    default = str(cfg.get_value(DEFAULT_MATURITY_KEY, DEFAULT_MATURITY))
    if product is None:
        return default
    return str(cfg.get_value(f"maturity.{product}", default))


def consensus_bar(cfg: ConfigStore, product: str | None = None) -> float:
    """Per-product jury consensus bar, falling back to ``default_consensus_bar``.

    Resolution order: ``consensus_bar.<product>`` -> ``default_consensus_bar`` ->
    :data:`DEFAULT_CONSENSUS_BAR`.
    """
    default = float(cfg.get_value(DEFAULT_CONSENSUS_BAR_KEY, DEFAULT_CONSENSUS_BAR))
    if product is None:
        return default
    return float(cfg.get_value(f"consensus_bar.{product}", default))


def jury_roster(cfg: ConfigStore) -> list[str]:
    """Resolve the jury roster (list of juror persona names)."""
    value = cfg.get_value(JURY_ROSTER_KEY, None)
    if not value:
        return list(DEFAULT_JURY_ROSTER)
    return [str(name) for name in value]
```

Extend `__all__` to include the new names (keep it sorted with the existing entries):

```python
__all__ = [
    "DEFAULT_CONSENSUS_BAR",
    "DEFAULT_CONSENSUS_BAR_KEY",
    "DEFAULT_MATURITY",
    "DEFAULT_MATURITY_KEY",
    "DEFAULT_THRESHOLD",
    "DEFAULT_THRESHOLD_KEY",
    "DEFAULT_WEIGHT",
    "JURY_ROSTER_KEY",
    "agent_enabled",
    "consensus_bar",
    "critic_enabled",
    "dry_run_global",
    "jury_roster",
    "maturity_level",
    "threshold",
    "triggers_paused",
    "weights",
]
```

- [x] **Step 4: Add the defaults**

In `config/defaults.json`, add `default_maturity` and `default_consensus_bar` after `default_threshold` (line 3), and a `jury` block after the `weight` block. The result:

```json
{
  "dry_run": true,
  "default_threshold": 0.6,
  "default_maturity": "supervised",
  "default_consensus_bar": 0.67,
  "critics": {
```

and at the end, after the closing brace of `weight`:

```json
  "weight": {
    "grounding": 1.0,
    "value": 1.0,
    "duplication": 1.0,
    "feasibility": 1.0,
    "strategic_fit": 1.0,
    "cost": 1.0,
    "security": 1.0
  },
  "jury": {
    "roster": ["pragmatist", "skeptic", "user_advocate"]
  }
}
```

- [x] **Step 5: Run tests to verify they pass**

Run: `uv run pytest core/tests/config/test_flags.py -v`
Expected: PASS (new tests plus existing).

- [x] **Step 6: Commit**

```bash
git add core/src/dsf/config/flags.py config/defaults.json core/tests/config/test_flags.py
git commit -m "feat(config): add maturity, consensus bar, and jury roster accessors"
```

---

## Task 6: The validation jury

**Files:**
- Create: `feature-council/src/dsf/council/jury.py`
- Test: `feature-council/tests/council/test_jury.py`

- [x] **Step 1: Write the failing test**

Create `feature-council/tests/council/test_jury.py`:

```python
"""Validation jury tests (validation-jury plan)."""

from __future__ import annotations

from dsf.container import build_services
from dsf.contracts.enums import Verdict
from dsf.contracts.models import CouncilVerdict, CriticScore
from dsf.council.jury import convene_jury
from dsf_testing import make_evidence, make_proposal, make_run


def _accept_recommendation(proposal_id: str) -> CouncilVerdict:
    return CouncilVerdict.from_scores(
        proposal_id, [CriticScore(critic="value", score=1.0)], threshold=0.6
    )


async def test_jury_offline_echoes_accept_recommendation():
    services = build_services("local")
    run = make_run([make_evidence("x")])
    prop = make_proposal(run)
    rec = _accept_recommendation(prop.id)
    assert rec.verdict == Verdict.ACCEPT

    jury = await convene_jury(rec, prop, run, services)

    assert len(jury.votes) == 3
    assert all(v.go for v in jury.votes)
    assert jury.consensus == 1.0


async def test_jury_offline_echoes_kill_recommendation():
    services = build_services("local")
    run = make_run([make_evidence("x")])
    prop = make_proposal(run)
    rec = CouncilVerdict.from_scores(prop.id, [], threshold=0.6)  # no scores -> KILL
    assert rec.verdict == Verdict.KILL

    jury = await convene_jury(rec, prop, run, services)

    assert all(not v.go for v in jury.votes)
    assert jury.consensus == 1.0
    assert jury.majority_go is False


async def test_jury_splits_when_one_model_dissents():
    services = build_services("local")
    services.model.register("[jury:skeptic]", lambda system, prompt: "NO-GO: evidence too thin")
    run = make_run([make_evidence("x")])
    prop = make_proposal(run)
    rec = _accept_recommendation(prop.id)

    jury = await convene_jury(rec, prop, run, services)

    assert sum(1 for v in jury.votes if v.go) == 2
    assert jury.majority_go is True
    assert abs(jury.go_fraction - 2 / 3) < 1e-9
    skeptic = next(v for v in jury.votes if v.juror == "skeptic")
    assert skeptic.go is False
    assert "thin" in skeptic.rationale.lower()
```

- [x] **Step 2: Run test to verify it fails**

Run: `uv run pytest feature-council/tests/council/test_jury.py -v`
Expected: FAIL with `ModuleNotFoundError: dsf.council.jury`.

- [x] **Step 3: Write the implementation**

Create `feature-council/src/dsf/council/jury.py`:

```python
"""Validation jury - a model-diverse panel that validates a council recommendation.

Each juror is a distinct persona that calls the model port with a juror-specific
tag. Offline (no registered handler) the model echoes, so each juror falls back
to the recommendation's own verdict; the panel then mirrors the deterministic
recommendation and the line stays green with no LLM. With real models (or scripted
test handlers) the jurors diverge and the panel does real validation work,
separating the judge from the proposer.
"""

from __future__ import annotations

from typing import TYPE_CHECKING

from dsf.config.flags import jury_roster
from dsf.contracts.enums import Verdict
from dsf.contracts.models import JurorVote, JuryResult
from dsf.model.client import ECHO_PREFIX

if TYPE_CHECKING:
    from dsf.container import Services
    from dsf.contracts.models import CouncilVerdict, Proposal, Run

_DEFAULT_PERSONA = "You are a careful reviewer. Vote GO or NO-GO."

#: Persona system prompts keyed by juror name.
_PERSONAS: dict[str, str] = {
    "pragmatist": (
        "You are a pragmatic engineer. Favor proposals that ship value soon "
        "with acceptable risk. Vote GO or NO-GO."
    ),
    "skeptic": (
        "You are a critical skeptic. Probe for weak evidence, hidden cost, and "
        "risk before agreeing. Vote GO or NO-GO."
    ),
    "user_advocate": (
        "You advocate for end users. Favor proposals that clearly improve the "
        "user experience. Vote GO or NO-GO."
    ),
}


def _vote_text(result: object) -> str:
    """Short rationale captured from a juror response (empty for echoes)."""
    if isinstance(result, str) and not result.startswith(ECHO_PREFIX):
        return result
    return ""


def _parse_vote(result: object, *, fallback: bool) -> bool:
    """Parse a juror's go/no-go from a model response.

    Falls back to ``fallback`` for the deterministic echo or any unparseable
    response. ``no-go`` is checked before ``go`` because it contains ``go``.
    """
    text = result if isinstance(result, str) else ""
    if not text or text.startswith(ECHO_PREFIX):
        return fallback
    low = text.lower()
    if "no-go" in low or "nogo" in low or "no go" in low or "reject" in low or "kill" in low:
        return False
    if "go" in low or "accept" in low or "proceed" in low:
        return True
    return fallback


async def convene_jury(
    recommendation: CouncilVerdict,
    proposal: Proposal,
    run: Run,
    services: Services,
) -> JuryResult:
    """Convene the validation jury over a council ``recommendation``."""
    fallback_go = recommendation.verdict == Verdict.ACCEPT
    votes: list[JurorVote] = []
    for persona in jury_roster(services.config):
        system = _PERSONAS.get(persona, _DEFAULT_PERSONA)
        prompt = (
            f"[jury:{persona}] Validate this council decision.\n"
            f"Proposal: {proposal.title}\n"
            f"Problem: {proposal.problem}\n"
            f"Recommendation: {recommendation.verdict.value} "
            f"(weighted score {recommendation.weighted_score:.2f} vs "
            f"threshold {recommendation.threshold:.2f}).\n"
            f"Rationale: {recommendation.rationale}\n"
            "Answer GO to proceed or NO-GO to reject."
        )
        result = await services.model.complete(system=system, prompt=prompt)
        votes.append(
            JurorVote(
                juror=persona,
                go=_parse_vote(result, fallback=fallback_go),
                rationale=_vote_text(result),
            )
        )
    return JuryResult(votes=votes)


__all__ = ["convene_jury"]
```

- [x] **Step 4: Run test to verify it passes**

Run: `uv run pytest feature-council/tests/council/test_jury.py -v`
Expected: PASS (3 tests).

- [x] **Step 5: Commit**

```bash
git add feature-council/src/dsf/council/jury.py feature-council/tests/council/test_jury.py
git commit -m "feat(council): add model-diverse validation jury"
```

---

## Task 7: Rewire decide() through the jury and outcome policy

**Files:**
- Modify: `feature-council/src/dsf/council/decision.py` (full rewrite)
- Test: `feature-council/tests/council/test_decision.py`

- [x] **Step 1: Write the failing tests**

Append to `feature-council/tests/council/test_decision.py` (the imports `build_services`, `make_evidence`, `make_proposal`, `make_run`, and `Verdict` already exist):

```python
async def test_jury_dissent_escalates_under_supervised():
    services = build_services("local")
    services.model.register("[jury:skeptic]", lambda system, prompt: "NO-GO")
    run = make_run([make_evidence("CRITICAL outage", confidence=0.9)])
    prop = make_proposal(run, proposed_change="Add a small cache.")
    verdict = await decide(prop, run, services)
    # ACCEPT recommendation, but a 2-1 jury under supervised maturity escalates.
    assert verdict.verdict == Verdict.ESCALATE
    assert verdict.jury is not None
    assert len(verdict.jury.votes) == 3


async def test_unanimous_jury_against_kills_a_strong_recommendation():
    services = build_services("local")
    for persona in ("pragmatist", "skeptic", "user_advocate"):
        services.model.register(f"[jury:{persona}]", lambda system, prompt: "NO-GO")
    run = make_run([make_evidence("CRITICAL outage", confidence=0.9)])
    prop = make_proposal(run, proposed_change="Add a small cache.")
    verdict = await decide(prop, run, services)
    assert verdict.verdict == Verdict.KILL


async def test_accept_path_populates_the_jury():
    services = build_services("local")
    run = make_run([make_evidence("CRITICAL outage", confidence=0.9)])
    prop = make_proposal(run, proposed_change="Add a small cache.")
    verdict = await decide(prop, run, services)
    assert verdict.verdict == Verdict.ACCEPT
    assert verdict.jury is not None
    assert len(verdict.jury.votes) == 3
    assert all(v.go for v in verdict.jury.votes)
```

- [x] **Step 2: Run tests to verify they fail**

Run: `uv run pytest feature-council/tests/council/test_decision.py::test_jury_dissent_escalates_under_supervised -v`
Expected: FAIL - `decide()` still returns ACCEPT (no jury wired) and `verdict.jury` is `None`.

- [x] **Step 3: Rewrite decision.py**

Replace the entire contents of `feature-council/src/dsf/council/decision.py` with:

```python
"""Decision engine - recommendation -> validation jury -> outcome policy.

The enabled critics produce a deterministic *recommendation* (any veto kills;
else the weighted score must clear the per-product threshold). A separate,
model-diverse *validation jury* then reviews that recommendation, and the
deterministic, maturity-gated *outcome policy* maps the jury onto the final
:class:`Verdict` (ACCEPT / ESCALATE / KILL). Offline the jury echoes the
recommendation, so the line behaves exactly as the critics decided until real
models (or a lower maturity dial) introduce escalation.
"""

from __future__ import annotations

from typing import TYPE_CHECKING

from dsf.config.flags import (
    consensus_bar,
    critic_enabled,
    maturity_level,
    threshold,
    weights,
)
from dsf.contracts.models import CouncilVerdict
from dsf.council.critics import ALL_CRITICS
from dsf.council.jury import convene_jury
from dsf.council.outcome import decide_outcome

if TYPE_CHECKING:
    from dsf.container import Services
    from dsf.contracts.models import Proposal, Run


async def _recommend(proposal: Proposal, run: Run, services: Services) -> CouncilVerdict:
    """Deterministic critic recommendation (the pre-jury proposer tier).

    Only critics with ``critic.<name>`` enabled (per the proposal's product)
    participate. The recommendation verdict and weighted score come from
    :meth:`CouncilVerdict.from_scores`, with weights resolved for exactly the
    enabled critics. A readable per-critic summary is attached for the audit log.
    """
    product = proposal.product
    enabled_names = [
        name
        for name in ALL_CRITICS
        if critic_enabled(services.config, name, product=product)
    ]

    scores = []
    for name in enabled_names:
        scores.append(await ALL_CRITICS[name](proposal, run, services))

    recommendation = CouncilVerdict.from_scores(
        proposal.id,
        scores,
        threshold(services.config, product=product),
        weights(services.config, enabled_names),
    )
    vetoes = [s.critic for s in scores if s.veto]
    recommendation.rationale = (
        f"{recommendation.rationale} "
        f"Critics ({len(scores)} enabled): "
        + ", ".join(f"{s.critic}={s.score:.2f}{'[VETO]' if s.veto else ''}" for s in scores)
        + (f". Vetoes: {', '.join(vetoes)}." if vetoes else ".")
    )
    return recommendation


async def decide(proposal: Proposal, run: Run, services: Services) -> CouncilVerdict:
    """Decide a proposal: critic recommendation, jury validation, outcome gate.

    The final verdict (ACCEPT / ESCALATE / KILL) comes from the maturity-gated
    outcome policy over the validation jury, not directly from the critics.
    """
    product = proposal.product
    recommendation = await _recommend(proposal, run, services)
    jury = await convene_jury(recommendation, proposal, run, services)
    verdict, outcome_rationale = decide_outcome(
        jury,
        maturity=maturity_level(services.config, product=product),
        consensus_bar=consensus_bar(services.config, product=product),
    )

    go = sum(1 for v in jury.votes if v.go)
    return CouncilVerdict(
        proposal_id=proposal.id,
        verdict=verdict,
        weighted_score=recommendation.weighted_score,
        threshold=recommendation.threshold,
        scores=recommendation.scores,
        jury=jury,
        rationale=(
            f"{outcome_rationale} Jury {go}/{len(jury.votes)} to proceed. "
            f"Recommendation: {recommendation.rationale}"
        ),
    )


__all__ = ["decide"]
```

- [x] **Step 4: Run the full decision test file to verify it passes**

Run: `uv run pytest feature-council/tests/council/test_decision.py -v`
Expected: PASS - the four original tests still pass (offline jury echoes the recommendation under the default supervised maturity) plus the three new ones.

- [x] **Step 5: Run the end-to-end dry-run line to confirm it stays green**

Run: `uv run pytest feature-council/tests/e2e/test_dry_run_line.py -v`
Expected: PASS - the offline jury is unanimous, so accepts still route and file.

- [x] **Step 6: Commit**

```bash
git add feature-council/src/dsf/council/decision.py feature-council/tests/council/test_decision.py
git commit -m "feat(council): route decisions through the validation jury and outcome policy"
```

---

## Task 8: S5 routes escalations to a human review queue

**Files:**
- Modify: `feature-council/src/dsf/orchestrator/stations/s5_council.py`
- Test: `feature-council/tests/orchestrator/test_s5_escalation.py`

- [x] **Step 1: Write the failing test**

Create `feature-council/tests/orchestrator/test_s5_escalation.py`:

```python
"""S5 routes ESCALATE outcomes to a human review queue (validation-jury plan)."""

from __future__ import annotations

from dsf.container import build_services
from dsf.orchestrator.blackboard import Blackboard
from dsf.orchestrator.stations.s5_council import REVIEW_QUEUE_KIND
from dsf.orchestrator.stations.s5_council import run as s5_run
from dsf_testing import make_evidence, make_proposal, make_run


async def test_escalated_proposal_goes_to_review_queue_not_routed():
    services = build_services("local")
    # Force a 2-1 jury under the default supervised maturity -> ESCALATE.
    services.model.register("[jury:skeptic]", lambda system, prompt: "NO-GO")

    run = make_run([make_evidence("CRITICAL outage", confidence=0.9)])
    prop = make_proposal(run, proposed_change="Add a small cache.")
    bb = Blackboard(services.memory)
    await bb.save_proposals(run.id, [prop])

    result = await s5_run(run, services)

    # Escalated proposals are not routed onward.
    assert result.proposals == []
    # A review-queue record was written for the proposal.
    queued = await services.memory.query_similar(prop.title, REVIEW_QUEUE_KIND, k=5)
    assert any(rec.get("proposal_id") == prop.id for rec in queued)
    # The audit trail records the escalation.
    assert any("escalated" in rec.message.lower() for rec in result.audit)
```

- [x] **Step 2: Run test to verify it fails**

Run: `uv run pytest feature-council/tests/orchestrator/test_s5_escalation.py -v`
Expected: FAIL with `ImportError` (no `REVIEW_QUEUE_KIND`).

- [x] **Step 3: Update S5 to handle three outcomes**

In `feature-council/src/dsf/orchestrator/stations/s5_council.py`, add the new record-kind constant after `KILL_LOG_KIND` (line 25):

```python
#: Memory record-kind for the accept/kill decision log.
KILL_LOG_KIND = "kill_log"

#: Memory record-kind for proposals escalated to human review.
REVIEW_QUEUE_KIND = "review_queue"
```

Replace the decision loop (the `for proposal in proposals:` block, lines 38-67) and the summary audit (lines 72-74) with:

```python
        accepted: list[Proposal] = []
        verdicts: list[CouncilVerdict] = []
        escalated = 0
        for proposal in proposals:
            verdict = await decide(proposal, run, services)
            verdicts.append(verdict)
            if verdict.verdict == Verdict.ACCEPT:
                accepted.append(proposal)
                # #3: index this proposal so future runs can detect duplicates
                await services.memory.put_record({
                    "kind": "proposal",
                    "text": f"{proposal.title} {proposal.problem}",
                    "proposal_id": proposal.id,
                    "run_id": run.id,
                })
                # #4: persist per-critic scores for later calibration join
                await services.memory.put_working(
                    f"critic_scores:{proposal.id}",
                    {s.critic: s.score for s in verdict.scores},
                )
            elif verdict.verdict == Verdict.ESCALATE:
                escalated += 1
                run.audit.append(
                    _audit(f"council escalated {proposal.id} to human review: {verdict.rationale}")
                )
                await services.memory.put_record(
                    {
                        "kind": REVIEW_QUEUE_KIND,
                        "run_id": run.id,
                        "proposal_id": proposal.id,
                        "verdict": verdict.verdict.value,
                        "weighted_score": verdict.weighted_score,
                        "threshold": verdict.threshold,
                        "text": f"{proposal.title} :: {verdict.rationale}",
                    }
                )
            else:
                run.audit.append(_audit(f"council killed {proposal.id}: {verdict.rationale}"))
                await services.memory.put_record(
                    {
                        "kind": KILL_LOG_KIND,
                        "run_id": run.id,
                        "proposal_id": proposal.id,
                        "verdict": verdict.verdict.value,
                        "weighted_score": verdict.weighted_score,
                        "threshold": verdict.threshold,
                        "text": f"{proposal.title} :: {verdict.rationale}",
                    }
                )

        await blackboard.save_proposals(run.id, accepted)
        await blackboard.save_verdicts(run.id, verdicts)
        run.proposals = [p.id for p in accepted]
        killed = len(proposals) - len(accepted) - escalated
        run.audit.append(
            _audit(
                f"council: {len(accepted)} accepted, {escalated} escalated, {killed} killed"
            )
        )
        return run
```

Update the `__all__` line at the bottom of the file:

```python
__all__ = ["STATION", "KILL_LOG_KIND", "REVIEW_QUEUE_KIND", "run"]
```

- [x] **Step 4: Run test to verify it passes**

Run: `uv run pytest feature-council/tests/orchestrator/test_s5_escalation.py -v`
Expected: PASS.

- [x] **Step 5: Commit**

```bash
git add feature-council/src/dsf/orchestrator/stations/s5_council.py feature-council/tests/orchestrator/test_s5_escalation.py
git commit -m "feat(orchestrator): route escalated proposals to a human review queue in S5"
```

---

## Task 9: Label escalated runs in consolidation

**Files:**
- Modify: `core/src/dsf/memory/consolidation.py:48`
- Test: `core/tests/memory/test_memory.py`

- [x] **Step 1: Write the failing test**

Add to `core/tests/memory/test_memory.py`. This file already imports everything the test needs (`Run`, `TriggerKind`, `RunStatus`, `Verdict`, `CouncilVerdict`, `consolidate_run`, `InMemoryMemoryStore`), so no import changes are required. Match the existing `test_consolidate_run_writes_retrievable_lesson` style (build `Run(...)` inline):

```python
async def test_consolidate_run_labels_escalated_outcome():
    store = InMemoryMemoryStore()
    run = Run(
        trigger=TriggerKind.SIGNAL,
        status=RunStatus.FILED,
        scope_product_hints=["microbi"],
    )
    verdict = CouncilVerdict(
        proposal_id="prop-1",
        verdict=Verdict.ESCALATE,
        weighted_score=0.5,
        threshold=0.6,
    )
    lesson = await consolidate_run(run, verdict, store)
    assert lesson["outcome"] == "escalated"
```

- [x] **Step 2: Run test to verify it fails**

Run: `uv run pytest core/tests/memory/test_memory.py::test_consolidate_run_labels_escalated_outcome -v`
Expected: FAIL - `lesson["outcome"]` is `"killed"`, not `"escalated"`.

- [x] **Step 3: Update the outcome mapping**

In `core/src/dsf/memory/consolidation.py`, replace line 48:

```python
    outcome = "accepted" if verdict.verdict == Verdict.ACCEPT else "killed"
```

with:

```python
    if verdict.verdict == Verdict.ACCEPT:
        outcome = "accepted"
    elif verdict.verdict == Verdict.ESCALATE:
        outcome = "escalated"
    else:
        outcome = "killed"
```

- [x] **Step 4: Run test to verify it passes**

Run: `uv run pytest core/tests/memory/test_memory.py -v`
Expected: PASS.

- [x] **Step 5: Commit**

```bash
git add core/src/dsf/memory/consolidation.py core/tests/memory/test_memory.py
git commit -m "feat(memory): label escalated runs in consolidation"
```

---

## Task 10: Full validation, docs, and plan close-out

**Files:**
- Modify: `docs/phases/feature-council.md` (the honest "today" note in the S5 / harness section)
- Modify: `docs/superpowers/specs/2026-06-19-feature-council-deliberative-redesign-design.md` (status note)
- Modify: `docs/adr/0011-feature-council-deliberative-redesign.md` (consequence note)
- Modify: this plan file (check off completed tasks)

- [x] **Step 1: Run the whole suite**

Run: `uv run pytest -q`
Expected: PASS, with the new tests added (the count should be the prior total plus the new tests from this plan).

- [x] **Step 2: Run lint**

Run: `uv run ruff check .`
Expected: `All checks passed!`

- [x] **Step 3: Run the eval gate**

Run: `uv run python -m dsf.evals.runner --gate`
Expected: gate PASSED (offline the verdicts are unchanged: supervised maturity plus a unanimous jury reproduce the prior accept/kill outcomes).

- [x] **Step 4: Run the import-linter contracts**

Run: `uv run lint-imports`
Expected: contracts kept (no app member imported by core; the jury contracts live in core, so no boundary is crossed).

- [x] **Step 5: Update the docs to reflect what is now real**

In `docs/phases/feature-council.md`, find the honest "today is deterministic critics" note in the S5 / harness section and update it to reflect that the validation jury, the maturity dial, and the escalate outcome now exist (the deliberation council and pull intake remain pending - Plans 2 and 3). Keep the prose humanizer-clean (no em dashes, no curly quotes, no AI-vocabulary, no emojis). Example replacement text for the S5 note:

```markdown
> Today the proposer tier is a set of deterministic critics, and a separate
> model-diverse validation jury reviews their recommendation. A deterministic,
> maturity-gated outcome policy then accepts, escalates to a human review queue,
> or kills. The multi-round deliberation council (Plan 2) and governed pull
> intake (Plan 3) are still pending.
```

In `docs/superpowers/specs/2026-06-19-feature-council-deliberative-redesign-design.md`, update the status line to note that Plan 1 (validation jury, outcome policy, maturity gate) is implemented and link this plan file.

In `docs/adr/0011-feature-council-deliberative-redesign.md`, update the consequences/"not built yet" note so it records that the validation jury, maturity gate, and escalate outcome have landed, with the deliberation council and pull intake still pending.

- [x] **Step 6: Verify the docs are humanizer-clean**

Run: `grep -nP "[\x{2014}\x{2013}\x{2018}\x{2019}\x{201C}\x{201D}]" docs/phases/feature-council.md docs/adr/0011-feature-council-deliberative-redesign.md docs/superpowers/specs/2026-06-19-feature-council-deliberative-redesign-design.md`
Expected: no output (no em/en dashes, no curly quotes).

- [x] **Step 7: Check off the completed tasks in this plan**

Edit `docs/superpowers/plans/2026-06-19-feature-council-validation-jury.md` and mark every completed `- [x]` as `- [x]`.

- [x] **Step 8: Commit**

```bash
git add docs/phases/feature-council.md docs/superpowers/specs/2026-06-19-feature-council-deliberative-redesign-design.md docs/adr/0011-feature-council-deliberative-redesign.md docs/superpowers/plans/2026-06-19-feature-council-validation-jury.md
git commit -m "docs: record landed validation jury, maturity gate, and escalate outcome"
```

---

## Self-Review Notes (for the executor)

- **The end-to-end dry-run line must stay green.** Tasks 7 and 10 both run `feature-council/tests/e2e/test_dry_run_line.py`. If it goes red, the most likely cause is the default maturity or consensus bar making the offline (unanimous) jury escalate instead of accept. The default must keep a unanimous GO accepting: `supervised` + `consensus_bar = 0.67` + a 3-juror unanimous panel gives consensus `1.0 >= 0.67`, which accepts.
- **`no-go` parsing order matters.** In `jury._parse_vote`, `no-go` is checked before `go` because the substring `go` is contained in `no-go`. Do not reorder.
- **Closure late-binding in the all-against test.** In Task 7's `test_unanimous_jury_against_kills_a_strong_recommendation`, every registered handler returns the same constant `"NO-GO"` regardless of persona, so the loop variable is not captured in a way that matters. If you change handlers to depend on `persona`, bind it with a default argument.
- **Per-product runtime tuning.** Maturity, consensus bar, and roster are read through `get_value` (seed-resolved), mirroring `threshold()` and `weights()`. Runtime tuning happens through the same mechanism as weights today (re-seed or the App Configuration adapter), consistent with governing the factory on the go. No new mutation API is introduced in this slice.
```
