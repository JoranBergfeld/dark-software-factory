"""Feature flags + product registry (control-center config layer)."""

from dsf.config.flags import (
    agent_enabled,
    critic_enabled,
    dry_run_global,
    threshold,
    triggers_paused,
    weights,
)
from dsf.config.registry import Product, load_registry, route_product

__all__ = [
    "Product",
    "agent_enabled",
    "critic_enabled",
    "dry_run_global",
    "load_registry",
    "route_product",
    "threshold",
    "triggers_paused",
    "weights",
]
