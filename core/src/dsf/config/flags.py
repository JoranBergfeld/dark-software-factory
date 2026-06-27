"""Typed accessors over a :class:`~dsf.ports.ConfigStore`.

These wrap the dotted flag/value naming convention established in Phase 0 so
callers never hardcode flag strings:

* ``critic.<name>``           -> :func:`critic_enabled`
* ``agent.<KIND>``            -> :func:`agent_enabled`
* ``trigger.<KIND>.paused``   -> :func:`triggers_paused`
* ``threshold.<product>`` (default key ``default_threshold``) -> :func:`threshold`
* ``weight.<critic>`` (default ``1.0``)                       -> :func:`weights`
"""

from __future__ import annotations

from typing import TYPE_CHECKING

from dsf.config.registry import Product
from dsf.contracts.enums import SourceKind, TriggerKind

if TYPE_CHECKING:
    from dsf.ports import ConfigStore

#: Fallback config key for the per-product confidence threshold.
DEFAULT_THRESHOLD_KEY = "default_threshold"
#: Hard fallback when ``default_threshold`` is itself unset.
DEFAULT_THRESHOLD = 0.6
#: Default weight for any critic without an explicit ``weight.<critic>`` value.
DEFAULT_WEIGHT = 1.0
#: Fallback config key for the per-product maturity dial.
DEFAULT_MATURITY_KEY = "default_maturity"
#: Hard fallback when ``default_maturity`` is itself unset.
DEFAULT_MATURITY = "supervised"
#: Fallback config key for the per-product jury consensus bar.
DEFAULT_CONSENSUS_BAR_KEY = "default_consensus_bar"
#: Hard fallback when ``default_consensus_bar`` is itself unset.
DEFAULT_CONSENSUS_BAR = 0.67
#: Config key for the jury roster (list of juror persona names).
JURY_ROSTER_KEY = "jury.roster"
#: Hard fallback roster when no ``jury.roster`` is configured.
DEFAULT_JURY_ROSTER = ("pragmatist", "skeptic", "user_advocate")
#: Fallback config key for the global number of deliberation rounds.
DEFAULT_DELIBERATION_ROUNDS_KEY = "default_deliberation_rounds"
#: Hard fallback when ``default_deliberation_rounds`` is itself unset. One to two
#: see-and-revise rounds is the design range; two is the deliberative default.
DEFAULT_DELIBERATION_ROUNDS = 2
#: Config flag gating the living-charter amendment loop (default off — opt-in).
CHARTER_AMENDMENT_ENABLED_FLAG = "charter.amendment.enabled"
#: Config key + fallback for the cooldown between factory amendment proposals.
CHARTER_AMENDMENT_COOLDOWN_HOURS_KEY = "charter.amendment.cooldown_hours"
DEFAULT_CHARTER_AMENDMENT_COOLDOWN_HOURS = 168.0
#: Config key + fallback for the minimum lessons required to justify an amendment.
CHARTER_AMENDMENT_MIN_LESSONS_KEY = "charter.amendment.min_lessons"
DEFAULT_CHARTER_AMENDMENT_MIN_LESSONS = 3


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


def product_record(cfg: ConfigStore, product: str) -> Product:
    """Load ``product``'s :class:`Product` record from its per-product App Config.

    Reads the unlabelled ``product.*`` keys the provisioner seeds and reuses the
    :func:`threshold` accessor for ``confidence_threshold``. Raises ``ValueError``
    if the record is absent (``product.github_repo`` unset) — a missing record is
    a provisioning fault that must fail loud, not silently de-scope the run.
    """
    github_repo = cfg.get_value("product.github_repo", "")
    if not github_repo:
        raise ValueError(
            f"no product record for {product!r} in App Configuration "
            "(product.github_repo is unset) — reprovision the factory"
        )
    return Product(
        key=product,
        github_repo=github_repo,
        label_taxonomy=cfg.get_value("product.label_taxonomy", {}) or {},
        foundryiq_scope=cfg.get_value("product.foundryiq_scope", "") or "",
        sentry_projects=cfg.get_value("product.sentry_projects", []) or [],
        grafana_dashboards=cfg.get_value("product.grafana_dashboards", []) or [],
        azure_monitor_scope=cfg.get_value("product.azure_monitor_scope", "") or "",
        confidence_threshold=threshold(cfg, product),
    )


def maturity_level(cfg: ConfigStore, product: str | None = None) -> str:
    """Per-product maturity dial, falling back to ``default_maturity``.

    Resolution order: ``maturity.<product>`` -> ``default_maturity`` ->
    :data:`DEFAULT_MATURITY`.
    """
    default = str(cfg.get_value(DEFAULT_MATURITY_KEY, DEFAULT_MATURITY))
    if product is None:
        return default
    return str(cfg.get_value(f"maturity.{product}", default))


def consensus_bar(cfg: ConfigStore, product: str | None = None) -> float:
    """Per-product jury consensus bar, falling back to ``default_consensus_bar``.

    Resolution order: ``consensus_bar.<product>`` -> ``default_consensus_bar`` ->
    :data:`DEFAULT_CONSENSUS_BAR`.
    """
    default = float(cfg.get_value(DEFAULT_CONSENSUS_BAR_KEY, DEFAULT_CONSENSUS_BAR))
    if product is None:
        return default
    return float(cfg.get_value(f"consensus_bar.{product}", default))


def jury_roster(cfg: ConfigStore) -> list[str]:
    """Resolve the jury roster (list of juror persona names)."""
    value = cfg.get_value(JURY_ROSTER_KEY, None)
    if not value:
        return list(DEFAULT_JURY_ROSTER)
    return [str(name) for name in value]


def deliberation_rounds(cfg: ConfigStore, product: str | None = None) -> int:
    """Per-product number of deliberation see-and-revise rounds.

    Resolution order: ``deliberation_rounds.<product>`` ->
    ``default_deliberation_rounds`` -> :data:`DEFAULT_DELIBERATION_ROUNDS`.
    Floored at 1 so the council always states at least one position.
    """
    default = int(cfg.get_value(DEFAULT_DELIBERATION_ROUNDS_KEY, DEFAULT_DELIBERATION_ROUNDS))
    if product is not None:
        default = int(cfg.get_value(f"deliberation_rounds.{product}", default))
    return max(1, default)


def charter_amendment_enabled(cfg: ConfigStore, product: str | None = None) -> bool:
    """Whether the factory may *propose* charter amendments for ``product``.

    Off by default (an LLM proposing changes to its own governing intent is a
    governance smell — opt-in per product via a control-center override).
    """
    return cfg.is_enabled(CHARTER_AMENDMENT_ENABLED_FLAG, product=product)


def charter_amendment_cooldown_hours(cfg: ConfigStore) -> float:
    """Minimum hours between successive factory amendment proposals (floored at 0)."""
    value = float(
        cfg.get_value(
            CHARTER_AMENDMENT_COOLDOWN_HOURS_KEY, DEFAULT_CHARTER_AMENDMENT_COOLDOWN_HOURS
        )
    )
    return max(0.0, value)


def charter_amendment_min_lessons(cfg: ConfigStore) -> int:
    """Minimum lessons that must back a proposal before one is drafted (floored at 1)."""
    value = int(
        cfg.get_value(CHARTER_AMENDMENT_MIN_LESSONS_KEY, DEFAULT_CHARTER_AMENDMENT_MIN_LESSONS)
    )
    return max(1, value)


__all__ = [
    "CHARTER_AMENDMENT_COOLDOWN_HOURS_KEY",
    "CHARTER_AMENDMENT_ENABLED_FLAG",
    "CHARTER_AMENDMENT_MIN_LESSONS_KEY",
    "DEFAULT_CHARTER_AMENDMENT_COOLDOWN_HOURS",
    "DEFAULT_CHARTER_AMENDMENT_MIN_LESSONS",
    "DEFAULT_CONSENSUS_BAR",
    "DEFAULT_CONSENSUS_BAR_KEY",
    "DEFAULT_DELIBERATION_ROUNDS",
    "DEFAULT_DELIBERATION_ROUNDS_KEY",
    "DEFAULT_MATURITY",
    "DEFAULT_MATURITY_KEY",
    "DEFAULT_THRESHOLD",
    "DEFAULT_THRESHOLD_KEY",
    "DEFAULT_WEIGHT",
    "JURY_ROSTER_KEY",
    "agent_enabled",
    "charter_amendment_cooldown_hours",
    "charter_amendment_enabled",
    "charter_amendment_min_lessons",
    "consensus_bar",
    "critic_enabled",
    "deliberation_rounds",
    "jury_roster",
    "maturity_level",
    "product_record",
    "threshold",
    "triggers_paused",
    "weights",
]
