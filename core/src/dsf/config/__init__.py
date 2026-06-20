"""Feature flags + product registry (control-center config layer)."""

from dsf.config.flags import (
    agent_enabled,
    critic_enabled,
    threshold,
    triggers_paused,
    weights,
)
from dsf.config.registry import Product, load_registry, route_product

__all__ = [
    "Product",
    "agent_enabled",
    "critic_enabled",
    "load_registry",
    "route_product",
    "threshold",
    "triggers_paused",
    "weights",
]
