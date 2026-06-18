"""Tests for tier-2 calibration weight recompute (plan Task 6.1)."""

from __future__ import annotations

from dsf.container import build_services
from dsf.learning.calibration import (
    MAX_WEIGHT,
    MIN_WEIGHT,
    proposed_weight_update,
    recompute_weights,
)


def test_predictive_critic_outranks_noisy_critic():
    # grounding: high score exactly when eventually approved (predictive)
    # cost: scores uncorrelated with approval (noisy)
    critic_scores = {
        "grounding": [
            (0.9, True),
            (0.85, True),
            (0.2, False),
            (0.1, False),
        ],
        "cost": [
            (0.5, True),
            (0.5, True),
            (0.5, False),
            (0.5, False),
        ],
    }
    weights = recompute_weights([], critic_scores)

    assert weights["grounding"] > weights["cost"]
    for w in weights.values():
        assert MIN_WEIGHT <= w <= MAX_WEIGHT


def test_anticorrelated_critic_is_penalized():
    critic_scores = {
        "good": [(0.9, True), (0.1, False)],
        "bad": [(0.1, True), (0.9, False)],  # high score => rejection
    }
    weights = recompute_weights([], critic_scores)
    assert weights["good"] > 1.0
    assert weights["bad"] < 1.0
    assert weights["bad"] >= MIN_WEIGHT


def test_clamped_bounds():
    critic_scores = {"perfect": [(1.0, True), (0.0, False)]}
    weights = recompute_weights([], critic_scores)
    assert weights["perfect"] <= MAX_WEIGHT


def test_no_history_returns_neutral():
    weights = recompute_weights([], {"x": []})
    assert weights["x"] == 1.0


async def test_proposed_update_returns_current_weights_without_history():
    services = build_services("local")
    proposed = await proposed_weight_update(services, ["grounding", "cost"])
    # No stored outcomes -> falls back to current configured weights.
    assert set(proposed) == {"grounding", "cost"}
    for w in proposed.values():
        assert isinstance(w, float)


async def test_proposed_update_uses_stored_scores():
    services = build_services("local")
    # Seed pr_outcome records carrying per-critic scores.
    await services.memory.put_record(
        {
            "kind": "pr_outcome",
            "verdict": "approved",
            "text": "pr_outcome approved",
            "critic_scores": {"grounding": 0.9, "cost": 0.5},
        }
    )
    await services.memory.put_record(
        {
            "kind": "pr_outcome",
            "verdict": "rejected",
            "text": "pr_outcome rejected",
            "critic_scores": {"grounding": 0.1, "cost": 0.5},
        }
    )
    proposed = await proposed_weight_update(services, ["grounding", "cost"])
    assert proposed["grounding"] > proposed["cost"]
