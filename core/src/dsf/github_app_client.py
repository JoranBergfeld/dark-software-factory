"""Real GitHub App identity client: mint short-lived, repo-scoped installation tokens.

The App private key never authenticates the API directly — it only signs a
<=10-minute RS256 JWT that exchanges for an installation access token. Tokens are
cached until just before expiry and re-minted on demand. Injecting ``transport``
(an ``httpx`` transport) and ``clock`` makes minting fully deterministic in tests
with no live call (ADR 0014 real-only ``src/``).
"""

from __future__ import annotations

from collections.abc import Callable
from dataclasses import dataclass, field
from datetime import UTC, datetime, timedelta

import httpx
import jwt

_GITHUB_API = "https://api.github.com"
_JWT_TTL = timedelta(minutes=9)  # GitHub caps App JWTs at 10 minutes
_REFRESH_SKEW = timedelta(seconds=60)  # re-mint slightly before expiry


def _utcnow() -> datetime:
    return datetime.now(UTC)


@dataclass
class _CachedToken:
    token: str
    expires_at: datetime


@dataclass
class GitHubAppClient:
    """Mints installation access tokens for the DSF GitHub App.

    ``repository_ids`` (when set) scopes minted tokens to exactly those repos.
    """

    app_id: str
    installation_id: str
    private_key_pem: str = field(repr=False)
    repository_ids: list[int] | None = None
    transport: httpx.BaseTransport | None = None
    clock: Callable[[], datetime] = _utcnow
    _cached: _CachedToken | None = field(default=None, init=False, repr=False)

    def _app_jwt(self) -> str:
        now = self.clock()
        payload = {
            "iat": int(now.timestamp()) - 60,
            "exp": int((now + _JWT_TTL).timestamp()),
            "iss": self.app_id,
        }
        return jwt.encode(payload, self.private_key_pem, algorithm="RS256")

    def installation_token(self) -> str:
        """Return a cached token, or mint a fresh repo-scoped installation token."""
        now = self.clock()
        if self._cached and self._cached.expires_at - _REFRESH_SKEW > now:
            return self._cached.token

        body: dict[str, object] = {}
        if self.repository_ids:
            body["repository_ids"] = self.repository_ids
        with httpx.Client(transport=self.transport, base_url=_GITHUB_API) as client:
            resp = client.post(
                f"/app/installations/{self.installation_id}/access_tokens",
                headers={
                    "Authorization": f"Bearer {self._app_jwt()}",
                    "Accept": "application/vnd.github+json",
                },
                json=body,
            )
            resp.raise_for_status()
            data = resp.json()
        expires_at = datetime.fromisoformat(data["expires_at"].replace("Z", "+00:00"))
        self._cached = _CachedToken(token=data["token"], expires_at=expires_at)
        return self._cached.token
