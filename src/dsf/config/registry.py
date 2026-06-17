"""Product Registry — config-as-data driving scoping (S1) and routing (S6).

A :class:`Product` carries everything the conveyor needs to scope a run to a
product and later route a surviving proposal to a repo + label taxonomy.
"""

from __future__ import annotations

import json
import re
from pathlib import Path

from pydantic import BaseModel, Field


def _repo_root() -> Path:
    """Locate repo root (where ``config/products.json`` lives)."""
    # src/dsf/config/registry.py -> repo root is three parents up from src/dsf.
    return Path(__file__).resolve().parents[3]


class Product(BaseModel):
    """A single product the intake line serves."""

    key: str
    github_repo: str
    label_taxonomy: dict[str, list[str]] = Field(default_factory=dict)
    foundryiq_scope: str = ""
    sentry_projects: list[str] = Field(default_factory=list)
    grafana_dashboards: list[str] = Field(default_factory=list)
    confidence_threshold: float = 0.6


def load_registry(path: str | Path | None = None) -> dict[str, Product]:
    """Load the product registry from ``config/products.json``.

    Returns a ``{key: Product}`` map. ``path`` overrides the default location.
    """
    target = Path(path) if path is not None else _repo_root() / "config" / "products.json"
    raw = json.loads(target.read_text(encoding="utf-8"))
    products = raw["products"] if isinstance(raw, dict) and "products" in raw else raw
    registry: dict[str, Product] = {}
    for entry in products:
        product = Product.model_validate(entry)
        registry[product.key] = product
    return registry


# Minimum key length for word-boundary fallback matching.
# Keys shorter than this (e.g. 3-char "api") are skipped to prevent mis-routing.
_MIN_KEY_LEN: int = 4


def route_product(hints: list[str], registry: dict[str, Product]) -> Product | None:
    """Match product ``hints`` to a registered product (case-insensitive).

    Matching is attempted in two passes for each hint, in order:

    1. **Exact match** (after strip + lowercase normalisation).
    2. **Word-boundary match**: the product key must appear as a complete token
       inside the hint.  Keys shorter than ``_MIN_KEY_LEN`` are skipped in this
       pass to prevent spurious matches from short or generic abbreviations.

    When multiple products match a single hint via word-boundary search the
    product whose key is longest (most specific) is returned; ties are broken
    by registry insertion order.

    Returns the first hint that produces any match, or ``None``.
    """
    for hint in hints:
        if not hint:
            continue
        h = hint.strip().lower()
        if not h:
            continue

        # Pass 1 - exact match.
        for key, product in registry.items():
            if key.lower() == h:
                return product

        # Pass 2 - word-boundary match (minimum key length enforced).
        matches: list[tuple[int, Product]] = []
        for key, product in registry.items():
            k = key.lower()
            if len(k) < _MIN_KEY_LEN:
                continue
            if re.search(r"\b" + re.escape(k) + r"\b", h):
                matches.append((len(k), product))

        if matches:
            # Longest key wins (most specific match); insertion order breaks ties.
            matches.sort(key=lambda x: x[0], reverse=True)
            return matches[0][1]

    return None



__all__ = ["Product", "load_registry", "route_product"]
