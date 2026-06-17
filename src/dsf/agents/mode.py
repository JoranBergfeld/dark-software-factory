"""Agent run-mode resolution + env helpers.

Each source agent's ``build_agent()`` selects its backend by mode:

* ``local`` (default) -> the deterministic fixture-backed fake backend.
* anything else (e.g. ``live`` / ``azure``) -> the real source backend, with a
  client constructed from environment variables.

Mode comes from the explicit argument, else the ``DSF_MODE`` env var, else
``local``. This keeps the whole line runnable with zero config while letting a
single agent talk to a real source when ``DSF_MODE=live`` and creds are present.
"""

from __future__ import annotations

import os

LOCAL = "local"


def resolve_mode(explicit: str | None = None) -> str:
    """Resolve the effective mode: explicit arg > ``DSF_MODE`` env > ``local``."""
    return (explicit or os.environ.get("DSF_MODE") or LOCAL).strip().lower()


def is_live(mode: str | None = None) -> bool:
    """True when the resolved mode means 'use the real source backend'."""
    return resolve_mode(mode) != LOCAL


def env_required(name: str, *, hint: str = "") -> str:
    """Return env var ``name`` or raise a clear error naming what's missing."""
    value = os.environ.get(name)
    if not value:
        suffix = f" ({hint})" if hint else ""
        raise RuntimeError(
            f"environment variable {name} is required for live mode{suffix}"
        )
    return value


__all__ = ["LOCAL", "resolve_mode", "is_live", "env_required"]
