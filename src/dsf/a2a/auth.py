"""Bearer-token auth as a FastAPI dependency.

Enforcement is opt-in and keyed on a single expected token:

* If the expected token is **non-empty**, every request must carry a matching
  ``Authorization: Bearer <token>`` header or it is rejected with HTTP 401.
* If the expected token is **empty/blank**, enforcement is disabled (open) —
  useful for local dry-run and tests that don't care about auth.

The default expected token is read from the ``A2A_BEARER_TOKEN`` env var.
"""

from __future__ import annotations

import os
from collections.abc import Callable

from fastapi import Header, HTTPException, status

#: Env var holding the expected bearer token (empty/unset disables enforcement).
BEARER_ENV_VAR = "A2A_BEARER_TOKEN"

_BEARER_PREFIX = "bearer "


def _extract_bearer(authorization: str | None) -> str | None:
    """Return the token portion of a ``Bearer`` header, or ``None``."""
    if not authorization:
        return None
    value = authorization.strip()
    if value.lower().startswith(_BEARER_PREFIX):
        return value[len(_BEARER_PREFIX) :].strip()
    return None


def build_bearer_dependency(expected_token: str | None) -> Callable[[str | None], None]:
    """Build a FastAPI dependency that enforces ``expected_token``.

    When ``expected_token`` is falsy (None/empty/blank), the returned dependency
    is a no-op (auth disabled). Otherwise it raises HTTP 401 on a missing,
    blank, or mismatched ``Authorization: Bearer`` header.
    """
    expected = (expected_token or "").strip()

    def _dependency(authorization: str | None = Header(default=None)) -> None:
        if not expected:
            return
        token = _extract_bearer(authorization)
        if not token or token != expected:
            raise HTTPException(
                status_code=status.HTTP_401_UNAUTHORIZED,
                detail="Invalid or missing bearer token",
                headers={"WWW-Authenticate": "Bearer"},
            )

    return _dependency


def require_bearer(expected_token: str | None = None) -> Callable[[str | None], None]:
    """Build a bearer dependency, defaulting the token from the environment.

    Pass ``expected_token=""`` to explicitly disable enforcement. Pass a real
    token to enforce it. Omit it to read :data:`BEARER_ENV_VAR` from the env.
    """
    if expected_token is None:
        expected_token = os.environ.get(BEARER_ENV_VAR, "")
    return build_bearer_dependency(expected_token)


__all__ = ["BEARER_ENV_VAR", "build_bearer_dependency", "require_bearer"]
