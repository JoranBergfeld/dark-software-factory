# Feature Council Deliberation Council Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace the parallel deterministic critic scoring in `council/decision.py` with a deliberation council: role-persona lens agents (value, cost, feasibility, security, strategic fit) that argue over one to two see-and-revise rounds, then a synthesizer that aggregates their positions into the recommendation. Grounding and duplication stay deterministic veto gates, not debating lenses.

**Architecture:** Today `_recommend()` runs every enabled critic and folds the scores with `CouncilVerdict.from_scores`. This slice splits the critics into two roles. The **gates** (grounding, duplication) keep running as deterministic checks that can veto. The **lenses** (value, cost, feasibility, security, strategic fit) become model-driven agents that deliberate: each states a position, reads the others, and revises. Offline, with no model handler registered, each lens falls back to its existing deterministic critic score, so the synthesized recommendation is byte-identical to today and every golden verdict holds. The synthesizer is the deterministic weighted aggregation (`from_scores`) over gates plus lenses, so the council's decision rule stays auditable even when the lens positions come from a model.

**Tech Stack:** Python 3.12, Pydantic v2, pytest (`asyncio_mode = auto`), uv workspace (`core` + `feature-council` members sharing the `dsf.*` namespace), import-linter boundaries (core may not import app members).

---

## Background the engineer needs

This is the second of three slices of the deliberative-council redesign recorded in ADR 0011 (`docs/adr/0011-feature-council-deliberative-redesign.md`) and the design spec `docs/superpowers/specs/2026-06-19-feature-council-deliberative-redesign-design.md`. Plan 1 (validation jury, maturity-gated outcome policy, escalate outcome) already landed: `decide()` now flows recommendation -> `convene_jury()` -> `decide_outcome()`. This slice (Plan 2) changes only how the *recommendation* is produced -- the proposer tier. The jury and outcome policy are untouched. Plan 3 (governed pull intake) is out of scope here.

Key invariants you must not break:

1. **The whole line runs offline and deterministically with NO model handlers registered.** The model port (`DeterministicModelClient`, `core/src/dsf/model/client.py`) returns an echo prefixed with `ECHO_PREFIX` (`"[deterministic]"`) when no handler matches a prompt tag. So every load-bearing lens must have a deterministic fallback. The lens fallback is its existing critic function in `ALL_CRITICS`: offline each lens returns exactly the `CriticScore` that critic produces today (same `critic` name, same `score`, same `veto`). The set of scores fed to `from_scores` is therefore identical to today, so `weighted_score` and the verdict are identical.

2. **Grounding and duplication stay deterministic veto gates.** The eval gate asserts a council-level veto (`adversarial-duplicate-veto` in `feature-council/src/dsf/evals/golden/cases.json` expects `must_veto: true`, which `veto_accuracy` checks as "at least one `CouncilVerdict.verdict == KILL`"). The duplication gate produces that KILL. So duplication must keep running in the decision path as a gate. It leaves the *debating* set (it is not a lens), but it is not removed. Same for grounding (the `adversarial-ungrounded-proposal` case disables `critic.grounding` and still files, proving the S4 grounding station is the real enforcement, but the gate stays in the council path for defense in depth and to honor `critic.grounding`).

3. **`critic.<name>` flags still gate both lenses and gates.** `test_disabling_a_critic_excludes_it_and_still_decides` disables `critic.security` and asserts the security score disappears from `verdict.scores` and the count drops by exactly one. Security is a lens, so `deliberate()` must skip a lens whose `critic.<name>` flag is off, per the proposal's product.

4. **`ALL_CRITICS` keeps all seven entries.** `test_all_critics_registry_has_seven` asserts the registry set. Do not delete any critic module; the lenses and gates both resolve their deterministic fallback through `ALL_CRITICS`.

5. **`core` may not import any app member** (import-linter). All new code in this slice lives in `feature-council`, except the one new config accessor, which lives in `core/src/dsf/config/flags.py` alongside the existing dials.

### Roles after this slice

| Role | Members | Behavior |
| --- | --- | --- |
| Gate (deterministic) | `grounding`, `duplication` | Run as today; a veto kills. Not debated. |
| Lens (deliberated) | `value`, `cost`, `feasibility`, `security`, `strategic_fit` | Each is a persona agent that scores its dimension over `deliberation.rounds` see-and-revise rounds; offline falls back to the deterministic critic. `security` can still veto. |
| Synthesizer | `CouncilVerdict.from_scores` | Weighted aggregation over gates + lenses; any veto kills, else weighted mean vs threshold. Deterministic and auditable. |

### Why the lenses fall back to the critics offline

The point of the slice is the *seam*: a real deployment registers model handlers so the lenses argue with genuine model diversity and revise across rounds. CI has no models, so the lenses must collapse to the deterministic critic scores that already drive the golden suite. This is the exact pattern Plan 1 used for the jury (`feature-council/src/dsf/council/jury.py`: `_parse_vote(..., fallback=...)`). We reuse it here for lens positions.

---

## File Structure

**Create:**
- `feature-council/src/dsf/council/deliberation.py` - the deliberation council: `LENS_NAMES`, `GATE_NAMES`, lens personas, `LensPosition`, `_parse_position`, `_lens_prompt`, and `deliberate(proposal, run, services) -> list[CriticScore]`.
- `feature-council/tests/council/test_deliberation.py` - deliberation unit tests.

**Modify:**
- `core/src/dsf/config/flags.py` - add `deliberation_rounds` accessor + key/default constants.
- `config/defaults.json` - add `deliberation.rounds`.
- `feature-council/src/dsf/council/decision.py` - rewire `_recommend()` to gates + `deliberate()` lenses + synthesize.
- `core/tests/config/test_flags.py` - extend with `deliberation_rounds` cases.
- `docs/phases/feature-council.md` - document the deliberation council.
- `docs/adr/0011-feature-council-deliberative-redesign.md` - consequence note for Plan 2.

**Validation commands (run from repo root):**
- Targeted: `uv run pytest <path>::<test> -v`
- Full suite: `uv run pytest -q` (baseline at branch point: 381 passed)
- Lint: `uv run ruff check .`
- Eval gate: `uv run python -m dsf.evals.runner --gate`
- Import contracts: `uv run lint-imports`

---

## Task 1: Add the `deliberation.rounds` config dial

**Files:** `core/src/dsf/config/flags.py`, `config/defaults.json`, `core/tests/config/test_flags.py`

The only new per-product dial this slice needs is the number of debate rounds. Maturity, consensus bar, and jury roster already exist (Plan 1); lens weights reuse the existing `weight.<critic>` block; lens enable/disable reuses the existing `critic.<name>` block.

### Step 1.1 (TDD): write the failing tests

Add to `core/tests/config/test_flags.py`:

```python
def test_deliberation_rounds_defaults_to_two():
    cfg = InMemoryConfigStore.from_defaults()
    assert deliberation_rounds(cfg) == 2


def test_deliberation_rounds_hard_fallback_when_unset():
    cfg = InMemoryConfigStore({})
    assert deliberation_rounds(cfg) == DEFAULT_DELIBERATION_ROUNDS


def test_deliberation_rounds_per_product_override():
    cfg = InMemoryConfigStore(
        {"default_deliberation_rounds": 2, "deliberation_rounds": {"alpha": 1}}
    )
    assert deliberation_rounds(cfg) == 2
    assert deliberation_rounds(cfg, product="alpha") == 1


def test_deliberation_rounds_is_floored_at_one():
    cfg = InMemoryConfigStore({"default_deliberation_rounds": 0})
    assert deliberation_rounds(cfg) == 1
```

Add `deliberation_rounds` and `DEFAULT_DELIBERATION_ROUNDS` to the import line at the top of `core/tests/config/test_flags.py` (it imports the names under test from `dsf.config.flags`). Check the existing import block and extend it; do not duplicate the import.

Run: `uv run pytest core/tests/config/test_flags.py -q` -> the four new tests fail (ImportError / NameError).

### Step 1.2: implement

This dial mirrors the existing `maturity_level` / `consensus_bar` / `threshold` pattern exactly: a **top-level** `default_<x>` key for the global value, and a **nested** `<x>.<product>` map for per-product overrides. This is required because `InMemoryConfigStore.get_value` (`core/src/dsf/config/store.py`) splits the key on `.` and walks into nested dicts: a single path cannot be both an `int` default and a per-product map. `set_flag` is irrelevant here -- it only stores boolean overrides read by `is_enabled`, not by `get_value`.

In `core/src/dsf/config/flags.py`, add the constants near the other defaults (after the consensus-bar block, around line 36):

```python
#: Fallback config key for the global number of deliberation rounds.
DEFAULT_DELIBERATION_ROUNDS_KEY = "default_deliberation_rounds"
#: Hard fallback when ``default_deliberation_rounds`` is itself unset. One to two
#: see-and-revise rounds is the design range; two is the deliberative default.
DEFAULT_DELIBERATION_ROUNDS = 2
```

Add the accessor near `consensus_bar` / `jury_roster`:

```python
def deliberation_rounds(cfg: ConfigStore, product: str | None = None) -> int:
    """Per-product number of deliberation see-and-revise rounds.

    Resolution order: ``deliberation_rounds.<product>`` ->
    ``default_deliberation_rounds`` -> :data:`DEFAULT_DELIBERATION_ROUNDS`.
    Floored at 1 so the council always states at least one position.
    """
    default = int(cfg.get_value(DEFAULT_DELIBERATION_ROUNDS_KEY, DEFAULT_DELIBERATION_ROUNDS))
    if product is not None:
        default = int(cfg.get_value(f"deliberation_rounds.{product}", default))
    return max(1, default)
```

Add `DEFAULT_DELIBERATION_ROUNDS`, `DEFAULT_DELIBERATION_ROUNDS_KEY`, and `deliberation_rounds` to `__all__`.

In `config/defaults.json`, add a **top-level** key (next to `default_consensus_bar`, before the `critics` block):

```json
  "default_deliberation_rounds": 2,
```

### Step 1.3: verify

- `uv run pytest core/tests/config/test_flags.py -q` -> all pass.
- `uv run ruff check core/src/dsf/config/flags.py` -> clean.

**Acceptance:** `deliberation_rounds` resolves default 2, per-product override, hard fallback, and floor at 1.

---

## Task 2: Create the deliberation council module (single round)

**Files:** `feature-council/src/dsf/council/deliberation.py` (new), `feature-council/tests/council/test_deliberation.py` (new)

This task builds the lens agents and a single-round `deliberate()`. Multi-round see-and-revise is added in Task 3 so this task stays small and the offline-parity property is proven first.

### Step 2.1 (TDD): write the failing tests

Create `feature-council/tests/council/test_deliberation.py`:

```python
"""Deliberation council tests."""

from __future__ import annotations

from dsf.container import build_services
from dsf.council.critics import ALL_CRITICS
from dsf.council.deliberation import (
    GATE_NAMES,
    LENS_NAMES,
    LensPosition,
    deliberate,
)
from dsf_testing import make_evidence, make_proposal, make_run


def test_lens_and_gate_partition():
    # Lenses are the debated dimensions; gates are the deterministic checks.
    assert set(LENS_NAMES) == {"value", "cost", "feasibility", "security", "strategic_fit"}
    assert set(GATE_NAMES) == {"grounding", "duplication"}
    # Every lens and gate is a real critic.
    assert set(LENS_NAMES) | set(GATE_NAMES) == set(ALL_CRITICS)


async def test_offline_lens_scores_match_the_deterministic_critics():
    services = build_services("local")
    run = make_run(
        [
            make_evidence("CRITICAL outage", confidence=0.9),
            make_evidence("high severity failure", confidence=0.9),
        ]
    )
    prop = make_proposal(run, proposed_change="Add a small cache.")

    positions = await deliberate(prop, run, services)
    by_name = {s.critic: s for s in positions}

    # Exactly the enabled lenses, no gates.
    assert set(by_name) == set(LENS_NAMES)

    # Each lens position equals the deterministic critic score offline.
    for name in LENS_NAMES:
        expected = await ALL_CRITICS[name](prop, run, services)
        assert by_name[name].score == expected.score
        assert by_name[name].veto == expected.veto


async def test_disabled_lens_is_excluded():
    services = build_services("local")
    services.config.set_flag("critic.cost", False)
    run = make_run([make_evidence("CRITICAL outage", confidence=0.9)])
    prop = make_proposal(run, proposed_change="Add a small cache.")

    positions = await deliberate(prop, run, services)
    assert all(s.critic != "cost" for s in positions)
    assert len(positions) == len(LENS_NAMES) - 1


async def test_security_lens_vetoes_offline():
    services = build_services("local")
    run = make_run([make_evidence("auth issue")])
    prop = make_proposal(
        run, proposed_change="store plaintext password to make login simpler"
    )
    positions = await deliberate(prop, run, services)
    security = next(s for s in positions if s.critic == "security")
    assert security.veto is True


async def test_registered_lens_handler_overrides_the_fallback():
    services = build_services("local")
    # A scripted value lens returns a structured position; the parser must use it
    # instead of the deterministic critic fallback.
    services.model.register(
        "[lens:value]",
        lambda system, prompt: LensPosition(score=0.123, veto=False, rationale="scripted"),
    )
    run = make_run([make_evidence("CRITICAL outage", confidence=0.9)])
    prop = make_proposal(run, proposed_change="Add a small cache.")

    positions = await deliberate(prop, run, services)
    value = next(s for s in positions if s.critic == "value")
    assert value.score == 0.123
    assert value.rationale == "scripted"
```

Run: `uv run pytest feature-council/tests/council/test_deliberation.py -q` -> fails (module does not exist).

### Step 2.2: implement `deliberation.py`

Create `feature-council/src/dsf/council/deliberation.py`:

```python
"""Deliberation council - role-persona lens agents that argue before scoring.

The five substantive decision lenses (value, cost, feasibility, security,
strategic fit) each state a position on a proposal through the model port. With
real models registered they deliberate with genuine perspective diversity; with
no handler registered the model echoes, so each lens falls back to its existing
deterministic critic in :data:`~dsf.council.critics.ALL_CRITICS`. The offline
positions are therefore identical to the critic scores that drive the golden
suite, which keeps the synthesized recommendation byte-identical to the pre-slice
behavior.

Grounding and duplication are *gates*, not lenses: they are matters of fact, run
deterministically in the decision engine, and can veto. They are listed here as
:data:`GATE_NAMES` only so the partition is documented in one place.

This module produces lens positions. The synthesis into a single recommendation
(weighted aggregation, veto handling, threshold) stays in
:func:`dsf.council.decision._recommend` via :meth:`CouncilVerdict.from_scores`,
so the council's decision rule remains deterministic and auditable.
"""

from __future__ import annotations

from typing import TYPE_CHECKING

from pydantic import BaseModel, Field

from dsf.config.flags import critic_enabled
from dsf.contracts.models import CriticScore
from dsf.council.critics import ALL_CRITICS
from dsf.model.client import ECHO_PREFIX

if TYPE_CHECKING:
    from dsf.container import Services
    from dsf.contracts.models import Proposal, Run

#: The debated decision lenses (the deterministic critics minus the gates).
LENS_NAMES: tuple[str, ...] = ("value", "cost", "feasibility", "security", "strategic_fit")

#: The deterministic veto gates (matters of fact, never debated).
GATE_NAMES: tuple[str, ...] = ("grounding", "duplication")

_DEFAULT_PERSONA = "You are a careful reviewer. Score this proposal on your lens from 0.0 to 1.0."

#: Persona system prompts keyed by lens name.
_PERSONAS: dict[str, str] = {
    "value": (
        "You weigh user and business value. Score higher when the evidence shows "
        "real, severe impact. Score 0.0 to 1.0."
    ),
    "cost": (
        "You weigh cost to build. Score higher when the change is small and cheap, "
        "lower when it implies large effort. Score 0.0 to 1.0."
    ),
    "feasibility": (
        "You weigh feasibility and delivery risk. Score lower for oversized or "
        "risky scope. Score 0.0 to 1.0."
    ),
    "security": (
        "You weigh security and compliance. Veto clearly unsafe changes; otherwise "
        "score 0.0 to 1.0."
    ),
    "strategic_fit": (
        "You weigh strategic fit with the product roadmap and prior lessons. Score "
        "0.0 to 1.0."
    ),
}


class LensPosition(BaseModel):
    """A lens agent's position on a proposal, as returned by the model port."""

    score: float = Field(ge=0.0, le=1.0)
    veto: bool = False
    rationale: str = ""


def _lens_prompt(
    name: str,
    proposal: Proposal,
    peers: dict[str, CriticScore],
    round_index: int,
) -> str:
    """Build the prompt for ``name``'s position in round ``round_index`` (0-based).

    Peer positions from the previous round are included from round 2 onward so
    each lens can see and revise against the others (the adversarial step).
    """
    header = (
        f"[lens:{name}] Round {round_index + 1}. Score this proposal on the "
        f"'{name}' lens from 0.0 (poor) to 1.0 (excellent). Veto only for a hard "
        f"blocker."
    )
    body = (
        f"Proposal: {proposal.title}\n"
        f"Problem: {proposal.problem}\n"
        f"Proposed change: {proposal.proposed_change}"
    )
    if not peers:
        return f"{header}\n{body}"
    peer_lines = "\n".join(
        f"- {peer}: {pos.score:.2f} {pos.rationale}".rstrip()
        for peer, pos in sorted(peers.items())
    )
    return f"{header}\n{body}\nPeer positions from the previous round:\n{peer_lines}"


def _parse_position(result: object, name: str, fallback: CriticScore) -> CriticScore:
    """Convert a model result into this lens's :class:`CriticScore`.

    A structured :class:`LensPosition` is adopted; a deterministic echo or any
    other shape falls back to the lens's critic score so the line stays green
    offline.
    """
    if isinstance(result, LensPosition):
        return CriticScore(
            critic=name,
            score=result.score,
            veto=result.veto,
            rationale=result.rationale,
        )
    if isinstance(result, str) and not result.startswith(ECHO_PREFIX):
        # Free-text model answer with no structured position: keep the
        # deterministic score but carry the prose for the audit log.
        return fallback.model_copy(update={"rationale": result})
    return fallback


async def _lens_position(
    name: str,
    proposal: Proposal,
    run: Run,
    services: Services,
    peers: dict[str, CriticScore],
    round_index: int,
) -> CriticScore:
    """Ask one lens for its position, falling back to its deterministic critic."""
    fallback = await ALL_CRITICS[name](proposal, run, services)
    persona = _PERSONAS.get(name, _DEFAULT_PERSONA)
    prompt = _lens_prompt(name, proposal, peers, round_index)
    result = await services.model.complete(system=persona, prompt=prompt, schema=LensPosition)
    return _parse_position(result, name, fallback)


async def deliberate(proposal: Proposal, run: Run, services: Services) -> list[CriticScore]:
    """Run the deliberation council and return one final position per enabled lens.

    Single round in this task; Task 3 extends it to ``deliberation.rounds``
    see-and-revise rounds. Only lenses whose ``critic.<name>`` flag is enabled
    for the proposal's product participate.
    """
    product = proposal.product
    enabled = [
        name for name in LENS_NAMES if critic_enabled(services.config, name, product=product)
    ]

    positions: dict[str, CriticScore] = {}
    for name in enabled:
        positions[name] = await _lens_position(name, proposal, run, services, {}, 0)
    return [positions[name] for name in enabled]


__all__ = ["GATE_NAMES", "LENS_NAMES", "LensPosition", "deliberate"]
```

> In this single-round task `deliberate()` does not yet read `deliberation_rounds`; Task 3 adds that import and the round loop. Keep the Task 2 import line as `from dsf.config.flags import critic_enabled` so there is no unused import.

### Step 2.3: verify

- `uv run pytest feature-council/tests/council/test_deliberation.py -q` -> all pass.
- `uv run ruff check feature-council/src/dsf/council/deliberation.py` -> clean.

**Acceptance:** offline lens positions equal the deterministic critic scores; disabled lens excluded; security lens vetoes; a registered handler overrides the fallback.

---

## Task 3: Add see-and-revise debate rounds

**Files:** `feature-council/src/dsf/council/deliberation.py`, `feature-council/tests/council/test_deliberation.py`

### Step 3.1 (TDD): write the failing tests

Append to `feature-council/tests/council/test_deliberation.py`:

```python
def _services_with_rounds(rounds: int):
    """Build local services whose config seeds a specific deliberation-round count.

    ``deliberation_rounds`` reads ``default_deliberation_rounds`` via
    ``get_value`` (not the boolean override path), so seed the store directly.
    ``Services`` is a mutable dataclass, so the config can be swapped after build.
    """
    from dsf.config.store import InMemoryConfigStore, load_defaults

    services = build_services("local")
    services.config = InMemoryConfigStore(
        {**load_defaults(), "default_deliberation_rounds": rounds}
    )
    return services


async def test_runs_one_model_call_per_lens_per_round():
    # Default rounds is 2 (config/defaults.json), so no seeding needed.
    services = build_services("local")
    run = make_run([make_evidence("CRITICAL outage", confidence=0.9)])
    prop = make_proposal(run, proposed_change="Add a small cache.")

    await deliberate(prop, run, services)
    # Five lenses x two rounds = ten model calls.
    lens_calls = [c for c in services.model.calls if "[lens:" in c[1]]
    assert len(lens_calls) == len(LENS_NAMES) * 2


async def test_second_round_sees_peer_positions_and_revises():
    services = build_services("local")  # default 2 rounds

    # The value lens scores 0.2 in round 1 (no peers in prompt) and 0.9 once it
    # sees peer positions in round 2. This proves peers are fed forward and the
    # final position is the revised one.
    def value_handler(system: str, prompt: str) -> LensPosition:
        if "Peer positions" in prompt:
            return LensPosition(score=0.9, veto=False, rationale="revised up after debate")
        return LensPosition(score=0.2, veto=False, rationale="initial")

    services.model.register("[lens:value]", value_handler)
    run = make_run([make_evidence("CRITICAL outage", confidence=0.9)])
    prop = make_proposal(run, proposed_change="Add a small cache.")

    positions = await deliberate(prop, run, services)
    value = next(s for s in positions if s.critic == "value")
    assert value.score == 0.9
    assert "revised" in value.rationale


async def test_offline_is_stable_across_rounds():
    # With no handlers, more rounds must not change the deterministic outcome.
    one = _services_with_rounds(1)
    two = _services_with_rounds(2)
    run = make_run([make_evidence("CRITICAL outage", confidence=0.9)])
    prop = make_proposal(run, proposed_change="Add a small cache.")

    p1 = {s.critic: s.score for s in await deliberate(prop, run, one)}
    p2 = {s.critic: s.score for s in await deliberate(prop, run, two)}
    assert p1 == p2
```

> **Why seed the store instead of `set_flag`:** `deliberation_rounds` reads `default_deliberation_rounds` through `get_value`, which only sees the seed dict (`_data`). `set_flag` writes a separate boolean-override map consulted only by `is_enabled`, so it cannot set an integer dial. The default of 2 from `config/defaults.json` covers the two-round tests with no setup.

Run: `uv run pytest feature-council/tests/council/test_deliberation.py -q` -> the new tests fail (only one round runs).

### Step 3.2: implement the round loop

In `feature-council/src/dsf/council/deliberation.py`, add the import back:

```python
from dsf.config.flags import critic_enabled, deliberation_rounds
```

Replace the body of `deliberate()`:

```python
async def deliberate(proposal: Proposal, run: Run, services: Services) -> list[CriticScore]:
    """Run the deliberation council and return one final position per enabled lens.

    Each enabled lens states a position; over ``deliberation.rounds`` rounds it
    re-states after seeing the others' previous-round positions (see-and-revise).
    Only lenses whose ``critic.<name>`` flag is enabled for the proposal's product
    participate. Offline (no model handler) every position is the lens's
    deterministic critic score and is stable across rounds.
    """
    product = proposal.product
    enabled = [
        name for name in LENS_NAMES if critic_enabled(services.config, name, product=product)
    ]
    rounds = deliberation_rounds(services.config, product=product)

    positions: dict[str, CriticScore] = {}
    for round_index in range(rounds):
        revised: dict[str, CriticScore] = {}
        for name in enabled:
            peers = {peer: pos for peer, pos in positions.items() if peer != name}
            revised[name] = await _lens_position(
                name, proposal, run, services, peers, round_index
            )
        positions = revised
    return [positions[name] for name in enabled]
```

### Step 3.3: verify

- `uv run pytest feature-council/tests/council/test_deliberation.py -q` -> all pass.
- `uv run ruff check feature-council/src/dsf/council/deliberation.py` -> clean (the `deliberation_rounds` import is now used).

**Acceptance:** lens count x rounds model calls; round 2 sees peers and adopts the revised position; offline is stable across round counts.

---

## Task 4: Wire the deliberation council into `_recommend`

**Files:** `feature-council/src/dsf/council/decision.py`, `feature-council/tests/council/test_decision.py`

This is the integration step. `_recommend` stops iterating `ALL_CRITICS` directly and instead runs the gates deterministically plus the lenses via `deliberate()`, then synthesizes with `from_scores` exactly as today.

### Step 4.1 (TDD): write the failing/parity tests

Append to `feature-council/tests/council/test_decision.py`:

```python
async def test_recommendation_is_offline_identical_to_critic_scoring():
    """The deliberation council, offline, reproduces the exact critic score set
    and weighted score the pre-slice critic loop produced."""
    from dsf.config.flags import threshold, weights
    from dsf.council.critics import ALL_CRITICS
    from dsf.council.decision import _recommend

    services = build_services("local")
    run = make_run(
        [
            make_evidence("CRITICAL outage", confidence=0.9),
            make_evidence("high severity failure", confidence=0.9),
        ]
    )
    prop = make_proposal(run, proposed_change="Add a small cache.")

    # Recompute what the old loop would have produced: every enabled critic.
    enabled = [
        name
        for name in ALL_CRITICS
        if services.config.is_enabled(f"critic.{name}", product=prop.product)
    ]
    expected_scores = [await ALL_CRITICS[n](prop, run, services) for n in enabled]
    expected = CouncilVerdict.from_scores(
        prop.id,
        expected_scores,
        threshold(services.config, product=prop.product),
        weights(services.config, enabled),
    )

    rec = await _recommend(prop, run, services)
    assert rec.weighted_score == expected.weighted_score
    assert rec.verdict == expected.verdict
    assert {s.critic for s in rec.scores} == set(enabled)


async def test_recommendation_carries_gate_and_lens_scores():
    from dsf.council.decision import _recommend
    from dsf.council.deliberation import GATE_NAMES, LENS_NAMES

    services = build_services("local")
    run = make_run([make_evidence("CRITICAL outage", confidence=0.9)])
    prop = make_proposal(run, proposed_change="Add a small cache.")

    rec = await _recommend(prop, run, services)
    names = {s.critic for s in rec.scores}
    assert names == set(GATE_NAMES) | set(LENS_NAMES)
```

Run: `uv run pytest feature-council/tests/council/test_decision.py -q`. The parity test should already pass against the current loop, but it pins the behavior before the refactor so a regression is caught. `test_recommendation_carries_gate_and_lens_scores` imports `deliberation` symbols, which exist from Task 2.

### Step 4.2: refactor `_recommend`

Rewrite the top of `feature-council/src/dsf/council/decision.py`. Replace the `ALL_CRITICS` import line with both imports:

```python
from dsf.council.critics import ALL_CRITICS
from dsf.council.deliberation import GATE_NAMES, deliberate
```

Replace the `_recommend` function body:

```python
async def _recommend(proposal: Proposal, run: Run, services: Services) -> CouncilVerdict:
    """Synthesize a recommendation from the deterministic gates and the lenses.

    The gates (grounding, duplication) run as deterministic checks that can veto;
    the lenses (value, cost, feasibility, security, strategic fit) deliberate via
    :func:`dsf.council.deliberation.deliberate`. Their positions are folded by
    :meth:`CouncilVerdict.from_scores`: any veto kills, else the weighted mean of
    the enabled gate and lens scores must clear the per-product threshold. Offline
    the lenses fall back to their critics, so the synthesis is identical to the
    pre-deliberation critic loop. A readable per-score summary is attached for the
    audit log.
    """
    product = proposal.product

    enabled_gates = [
        name for name in GATE_NAMES if critic_enabled(services.config, name, product=product)
    ]
    gate_scores = [await ALL_CRITICS[name](proposal, run, services) for name in enabled_gates]
    lens_scores = await deliberate(proposal, run, services)

    scores = gate_scores + lens_scores
    scored_names = [s.critic for s in scores]

    recommendation = CouncilVerdict.from_scores(
        proposal.id,
        scores,
        threshold(services.config, product=product),
        weights(services.config, scored_names),
    )
    vetoes = [s.critic for s in scores if s.veto]
    recommendation.rationale = (
        f"{recommendation.rationale} "
        f"Gates ({len(gate_scores)}) + lenses ({len(lens_scores)}): "
        + ", ".join(f"{s.critic}={s.score:.2f}{'[VETO]' if s.veto else ''}" for s in scores)
        + (f". Vetoes: {', '.join(vetoes)}." if vetoes else ".")
    )
    return recommendation
```

Leave `decide()` unchanged: it already calls `_recommend` then `convene_jury` then `decide_outcome`.

### Step 4.3: verify (the whole gauntlet)

- `uv run pytest feature-council/tests/council/test_decision.py -q` -> all pass (the Plan 1 jury/escalate tests included).
- `uv run pytest -q` -> full suite green (expect 381 + the new deliberation/flags tests).
- `uv run ruff check .` -> clean.
- `uv run python -m dsf.evals.runner --gate` -> PASSED (verdict_match unchanged because offline scores are identical).
- `uv run lint-imports` -> 4 contracts kept.

**Acceptance:** `_recommend` produces gate + lens scores; the offline weighted score and verdict are identical to the pre-slice critic loop; the whole gauntlet is green.

---

## Task 5: Document the deliberation council

**Files:** `docs/phases/feature-council.md`, `docs/adr/0011-feature-council-deliberative-redesign.md`, this plan doc.

### Step 5.1: phase doc

In `docs/phases/feature-council.md`, find the S5 / council decision section and add a "Deliberation council" subsection describing: the gate/lens partition; the see-and-revise rounds; that the synthesizer is the deterministic weighted aggregation; the offline fallback to critics; and the dials (`deliberation.rounds`, `critic.<name>` lens enable, `weight.<name>` lens influence). Add or update a small diagram (ASCII or Mermaid) of: proposal -> [gates: grounding, duplication] + [lenses deliberate N rounds] -> synthesize (from_scores) -> recommendation -> jury -> outcome. Keep it consistent with the existing diagram style in that file.

### Step 5.2: ADR consequence note

In `docs/adr/0011-feature-council-deliberative-redesign.md`, extend the consequences/status section to record: Plan 2 (deliberation council) landed; lenses are model agents with deterministic critic fallback offline; grounding and duplication are kept as deterministic veto gates (not debating lenses), so the eval goldens (`adversarial-duplicate-veto`, `adversarial-ungrounded-proposal`) are untouched; the S3 evidence-synthesis station was deliberately not relocated into S5 (the grounding gate at S4 must run on proposals before the council, so S3 stays; the "synthesizer" in this slice is the recommendation aggregation, not proposal synthesis); only new dial is `deliberation.rounds`.

### Step 5.3: check off this plan

Mark the tasks in this plan doc complete (`- [x]`).

### Step 5.4: verify prose is humanizer-clean

Run:

```bash
grep -nP "[\x{2014}\x{2013}\x{2018}\x{2019}\x{201C}\x{201D}]" docs/phases/feature-council.md docs/adr/0011-feature-council-deliberative-redesign.md
```

Expect no output. Avoid AI-vocabulary (robust, leverage, seamless, crucial, delve) and emojis. No code tests for docs.

**Acceptance:** the phase doc and ADR describe the deliberation council accurately; prose is humanizer-clean.

---

## Definition of done

- [x] Task 1 - `deliberation.rounds` dial + accessor + tests.
- [x] Task 2 - deliberation module (single round) + tests; offline parity with critics proven.
- [x] Task 3 - see-and-revise rounds + tests.
- [x] Task 4 - `_recommend` wired to gates + deliberation; full gauntlet green.
- [x] Task 5 - docs + ADR + plan check-off; humanizer-clean.

**Final gate (all must pass):**
- `uv run pytest -q`
- `uv run ruff check .`
- `uv run python -m dsf.evals.runner --gate`
- `uv run lint-imports`
