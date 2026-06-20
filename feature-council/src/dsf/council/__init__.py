"""Critic Council — synthesizer, seven critics, and the decision engine.

Phase 3 of the intake line. The synthesizer turns a run's evidence into
candidate proposals; the critics each score a proposal through a distinct
lens; the decision engine aggregates enabled critics into a verdict.

Everything works even when the model returns no structured output: the model is
used only for the proposal title, while ``evidence_ids``, ``product`` and
``kind`` are derived deterministically.
"""

from __future__ import annotations
