"""Evals — golden set, evaluators, and the CI gate runner (plan Task 8.1)."""

from __future__ import annotations

from dsf.evals.evaluators import groundedness, routing_accuracy, verdict_match

__all__ = ["groundedness", "routing_accuracy", "verdict_match"]
