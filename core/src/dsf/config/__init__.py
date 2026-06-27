"""Feature flags + product record (control-center config layer)."""

from dsf.config.flags import (
    agent_enabled,
    critic_enabled,
    product_record,
    threshold,
    triggers_paused,
    weights,
)
from dsf.config.registry import Product

__all__ = [
    "Product",
    "agent_enabled",
    "critic_enabled",
    "product_record",
    "threshold",
    "triggers_paused",
    "weights",
]
