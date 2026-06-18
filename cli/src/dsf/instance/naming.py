"""Derive Azure-safe resource name prefixes for an instance.

Azure's ``namePrefix`` Bicep parameter is 3-12 lowercase characters and must start
with a letter (Key Vault and similar resources reject leading digits). We sanitize
the user-supplied base, substring it, and append a short random token so re-created
instances never collide with a prior deployment's globally-unique names — and dodge
Key Vault's 90-day soft-delete name reservation.
"""

from __future__ import annotations

import re
import secrets

_ALPHABET = "abcdefghijklmnopqrstuvwxyz0123456789"
_MAX_LEN = 12
_VALID = re.compile(r"^[a-z][a-z0-9]{2,11}$")


def _random_token(length: int) -> str:
    """Return a random lowercase-alphanumeric token of the given length."""
    return "".join(secrets.choice(_ALPHABET) for _ in range(length))


def make_name_prefix(base: str, *, token: str | None = None, token_len: int = 4) -> str:
    """Return an Azure-safe effective name prefix derived from ``base``.

    ``base`` is lowercased and stripped to alphanumerics, must start with a letter,
    is substringed to leave room for a ``token_len``-char token, and the token
    (random by default; inject ``token`` for deterministic tests) is appended.
    Raises ``ValueError`` if the base or derived prefix is invalid.
    """
    cleaned = "".join(c for c in base.lower() if c.isalnum())
    if not cleaned or not cleaned[0].isalpha():
        raise ValueError(f"name prefix base must start with a letter: {base!r}")
    stem = cleaned[: _MAX_LEN - token_len]
    tok = token if token is not None else _random_token(token_len)
    prefix = f"{stem}{tok}"
    if not _VALID.match(prefix):
        raise ValueError(f"derived name prefix is invalid: {prefix!r}")
    return prefix
