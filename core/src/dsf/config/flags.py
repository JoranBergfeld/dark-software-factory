"""Typed accessors over a :class:`~dsf.ports.ConfigStore`.

These wrap the dotted flag/value naming convention established in Phase 0 so
callers never hardcode flag strings:

* ``critic.<name>``           -> :func:`critic_enabled`
* ``agent.<KIND>``            -> :func:`agent_enabled`
* ``trigger.<KIND>.paused``   -> :func:`triggers_paused`
* ``dry_run``                 -> :func:`dry_run_global`
* ``threshold.<product>`` (default key ``default_threshold``) -> :func:`threshold`
* ``weight.<critic>`` (default ``1.0``)                       -> :func:`weights`
"""

from __future__ import annotations

from typing import TYPE_CHECKING

from dsf.contracts.enums import SourceKind, TriggerKind

if TYPE_CHECKING:
    from dsf.ports import ConfigStore

#: Fallback config key for the per-product confidence threshold.
DEFAULT_THRESHOLD_KEY = "default_threshold"
#: Hard fallback when ``default_threshold`` is itself unset.
DEFAULT_THRESHOLD = 0.6
#: Default weight for any critic without an explicit ``weight.<critic>`` value.
DEFAULT_WEIGHT = 1.0


def critic_enabled(cfg: ConfigStore, name: str, product: str | None = None) -> bool:
    """Whether the ``name`` critic is enabled (optionally per-product)."""
    return cfg.is_enabled(f"critic.{name}", product=product)


def agent_enabled(cfg: ConfigStore, kind: SourceKind | str) -> bool:
    """Whether the source agent for ``kind`` is enabled.

    ``kind`` may be a :class:`SourceKind` or its uppercase string value.
    """
    key = kind.value if isinstance(kind, SourceKind) else str(kind)
    return cfg.is_enabled(f"agent.{key}")


def triggers_paused(cfg: ConfigStore, trigger_kind: TriggerKind | str) -> bool:
    """Whether the ``trigger_kind`` trigger (SCHEDULED|SIGNAL) is paused."""
    key = trigger_kind.value if isinstance(trigger_kind, TriggerKind) else str(trigger_kind)
    return cfg.is_enabled(f"trigger.{key}.paused")


def dry_run_global(cfg: ConfigStore) -> bool:
    """Whether the global dry-run kill switch is on."""
    return cfg.is_enabled("dry_run")


def threshold(cfg: ConfigStore, product: str | None = None) -> float:
    """Per-product confidence threshold, falling back to ``default_threshold``.

    Resolution order: ``threshold.<product>`` -> ``default_threshold`` ->
    :data:`DEFAULT_THRESHOLD`.
    """
    default = float(cfg.get_value(DEFAULT_THRESHOLD_KEY, DEFAULT_THRESHOLD))
    if product is None:
        return default
    return float(cfg.get_value(f"threshold.{product}", default))


def weights(cfg: ConfigStore, critics: list[str]) -> dict[str, float]:
    """Resolve a ``{critic: weight}`` map for the given ``critics``.

    Each weight comes from ``weight.<critic>`` and defaults to
    :data:`DEFAULT_WEIGHT`.
    """
    return {name: float(cfg.get_value(f"weight.{name}", DEFAULT_WEIGHT)) for name in critics}


__all__ = [
    "DEFAULT_THRESHOLD",
    "DEFAULT_THRESHOLD_KEY",
    "DEFAULT_WEIGHT",
    "agent_enabled",
    "critic_enabled",
    "dry_run_global",
    "threshold",
    "triggers_paused",
    "weights",
]
