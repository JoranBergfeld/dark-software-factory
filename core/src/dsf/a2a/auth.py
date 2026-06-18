"""Bearer-token auth as a FastAPI dependency.

Enforcement behavior:

* If the expected token is **non-empty**, every request must carry a matching
  ``Authorization: Bearer <token>`` header or it is rejected with HTTP 401.
* If the expected token is **empty/blank** and the resolved mode is ``local``,
  enforcement is disabled (open) -- safe for local dry-run and tests.
* If the expected token is **empty/blank** and the resolved mode is NOT ``local``,
  a :exc:`RuntimeError` is raised immediately at setup time (fail CLOSED) -- this
  prevents accidentally serving an unauthenticated endpoint in any live
  environment.

Comparison always uses :func:`hmac.compare_digest` to prevent timing attacks.
"""

from __future__ import annotations

import hmac
import os
from collections.abc import Callable

from fastapi import Header, HTTPException, status

#: Env var holding the expected bearer token (empty/unset disables enforcement in local mode).
BEARER_ENV_VAR = "A2A_BEARER_TOKEN"

#: The canonical local-mode string (mirrors dsf.agents.mode.LOCAL).
_LOCAL = "local"

_BEARER_PREFIX = "bearer "


def _resolve_effective_mode(explicit: str | None) -> str:
    """Resolve the run mode without importing dsf.agents (avoids circular import).

    Priority: explicit argument > ``DSF_MODE`` env var > ``local``.
    """
    return (explicit or os.environ.get("DSF_MODE") or _LOCAL).strip().lower()


def _extract_bearer(authorization: str | None) -> str | None:
    """Return the token portion of a ``Bearer`` header, or ``None``."""
    if not authorization:
        return None
    value = authorization.strip()
    if value.lower().startswith(_BEARER_PREFIX):
        return value[len(_BEARER_PREFIX) :].strip()
    return None


def build_bearer_dependency(
    expected_token: str | None,
    *,
    mode: str | None = None,
) -> Callable[[str | None], None]:
    """Build a FastAPI dependency that enforces ``expected_token``.

    Raises :exc:`RuntimeError` at call time if no token is provided outside
    local mode (fail CLOSED).  In local mode an empty token produces a no-op
    dependency (auth disabled).  Comparison is constant-time via
    :func:`hmac.compare_digest`.

    Parameters
    ----------
    expected_token:
        The secret the caller must present.  Falsy/blank = no token configured.
    mode:
        Explicit run mode string.  ``None`` defers to the ``DSF_MODE`` env var,
        falling back to ``local``.
    """
    expected = (expected_token or "").strip()
    effective_mode = _resolve_effective_mode(mode)

    if not expected and effective_mode != _LOCAL:
        raise RuntimeError(
            f"{BEARER_ENV_VAR} must be set when DSF_MODE is {effective_mode!r}. "
            f"Set {BEARER_ENV_VAR} to a secret token, or set DSF_MODE=local "
            "to run without bearer authentication."
        )

    def _dependency(authorization: str | None = Header(default=None)) -> None:
        if not expected:
            return  # local mode: auth disabled
        token = _extract_bearer(authorization)
        if not token or not hmac.compare_digest(token, expected):
            raise HTTPException(
                status_code=status.HTTP_401_UNAUTHORIZED,
                detail="Invalid or missing bearer token",
                headers={"WWW-Authenticate": "Bearer"},
            )

    return _dependency


def require_bearer(
    expected_token: str | None = None,
    *,
    mode: str | None = None,
) -> Callable[[str | None], None]:
    """Build a bearer dependency, defaulting the token from the environment.

    Pass ``expected_token=""`` to explicitly disable enforcement (local/test).
    Pass a real token to enforce it.  Omit it to read :data:`BEARER_ENV_VAR`
    from the env.  In non-local mode, an unset env var raises immediately.
    """
    if expected_token is None:
        expected_token = os.environ.get(BEARER_ENV_VAR, "")
    return build_bearer_dependency(expected_token, mode=mode)


__all__ = ["BEARER_ENV_VAR", "build_bearer_dependency", "require_bearer"]