"""Product Registry — config-as-data driving scoping (S1) and routing (S6).

A :class:`Product` carries everything the conveyor needs to scope a run to a
product and later route a surviving proposal to a repo + label taxonomy.
"""

from __future__ import annotations

import json
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


def route_product(hints: list[str], registry: dict[str, Product]) -> Product | None:
    """Match product ``hints`` to a registered product (case-insensitive).

    A hint matches a product when the product key is a substring of the hint
    (or vice versa). Returns the first match, or ``None`` if no hint matches.
    """
    for hint in hints:
        if not hint:
            continue
        h = hint.strip().lower()
        if not h:
            continue
        for key, product in registry.items():
            k = key.lower()
            if k == h or k in h or h in k:
                return product
    return None


__all__ = ["Product", "load_registry", "route_product"]
