"""The seven independent critics of the council.

Each critic module exposes ``NAME`` and an async ``evaluate(proposal, run,
services) -> CriticScore``. :data:`ALL_CRITICS` maps each critic name to its
``evaluate`` callable so the decision engine can iterate enabled critics.
"""

from __future__ import annotations

from collections.abc import Awaitable, Callable

from dsf.contracts.models import CriticScore
from dsf.council.critics import (
    cost,
    duplication,
    feasibility,
    grounding,
    security,
    strategic_fit,
    value,
)

#: Type of a critic's evaluate function.
CriticFn = Callable[..., Awaitable[CriticScore]]

#: name -> evaluate callable for every critic.
ALL_CRITICS: dict[str, CriticFn] = {
    grounding.NAME: grounding.evaluate,
    value.NAME: value.evaluate,
    duplication.NAME: duplication.evaluate,
    feasibility.NAME: feasibility.evaluate,
    strategic_fit.NAME: strategic_fit.evaluate,
    cost.NAME: cost.evaluate,
    security.NAME: security.evaluate,
}

__all__ = ["ALL_CRITICS", "CriticFn"]
