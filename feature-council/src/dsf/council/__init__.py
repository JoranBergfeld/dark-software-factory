"""Critic Council — synthesizer, seven critics, and the decision engine.

Phase 3 of the intake line. The synthesizer turns a run's evidence into
candidate proposals; the critics each score a proposal through a distinct
lens; the decision engine aggregates enabled critics into a verdict.

Everything works in local dry-run with the default :class:`~dsf.model.DeterministicModelClient`
behaviour (no registered handler): the model is used only for prose, while
``evidence_ids``, ``product`` and ``kind`` are derived deterministically.
"""

from __future__ import annotations
