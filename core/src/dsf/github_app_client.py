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
_COPILOT_LOGIN = "copilot-swe-agent"
_SUGGESTED_ACTORS_QUERY = (
    "query($owner:String!,$name:String!){"
    "repository(owner:$owner,name:$name){"
    "suggestedActors(capabilities:[CAN_BE_ASSIGNED],first:100){"
    "nodes{login __typename ... on Bot{id} ... on User{id}}}}}"
)
_REPLACE_ACTORS_MUTATION = (
    "mutation($assignableId:ID!,$actorIds:[ID!]!){"
    "replaceActorsForAssignable(input:{assignableId:$assignableId,actorIds:$actorIds}){"
    "assignable{__typename}}}"
)


def _utcnow() -> datetime:
    return datetime.now(UTC)


@dataclass
class _CachedToken:
    token: str
    expires_at: datetime


@dataclass
class GitHubAppClient:
    """Mints installation access tokens for the DSF GitHub App.

    ``repository_ids`` / ``repositories`` (when set) scope minted tokens to exactly
    those repos (by numeric id or by name, respectively).
    """

    app_id: str
    installation_id: str
    private_key_pem: str = field(repr=False)
    repository_ids: list[int] | None = None
    repositories: list[str] | None = None
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
        if self.repositories:
            body["repositories"] = self.repositories
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

    def _token_headers(self, token: str) -> dict[str, str]:
        return {
            "Authorization": f"Bearer {token}",
            "Accept": "application/vnd.github+json",
        }

    async def _graphql(
        self, client: httpx.AsyncClient, token: str, query: str, variables: dict
    ) -> dict:
        resp = await client.post(
            "/graphql",
            headers=self._token_headers(token),
            json={"query": query, "variables": variables},
        )
        resp.raise_for_status()
        data = resp.json()
        if data.get("errors"):
            raise RuntimeError(f"GraphQL error: {data['errors']}")
        return data["data"]

    async def assign_coding_agent(self, repo: str, issue_node_id: str) -> None:
        """Assign the Copilot coding agent (``copilot-swe-agent``) to an issue.

        Bots are not REST-assignable, so this uses GraphQL: resolve the bot's node
        id from the repo's suggested actors, then ``replaceActorsForAssignable``.
        Raises ``RuntimeError`` if Copilot is not an assignable actor on ``repo``.
        """
        owner, _, name = repo.partition("/")
        token = self.installation_token()
        async with httpx.AsyncClient(transport=self.transport, base_url=_GITHUB_API) as client:
            data = await self._graphql(
                client, token, _SUGGESTED_ACTORS_QUERY, {"owner": owner, "name": name}
            )
            nodes = data["repository"]["suggestedActors"]["nodes"]
            bot_id = next((n["id"] for n in nodes if n.get("login") == _COPILOT_LOGIN), None)
            if bot_id is None:
                raise RuntimeError(
                    f"{_COPILOT_LOGIN} is not an assignable actor on {repo}; "
                    "ensure GitHub Copilot coding agent is enabled for the repo"
                )
            await self._graphql(
                client,
                token,
                _REPLACE_ACTORS_MUTATION,
                {"assignableId": issue_node_id, "actorIds": [bot_id]},
            )

    async def create_issue(self, repo: str, title: str, body: str, labels: list[str]) -> str:
        """File an issue and hand it to the Copilot coding agent; return its URL.

        Files via REST, captures the issue node id, then assigns the coding agent
        (Feature Council's output is a build request for the agent). Satisfies the
        :class:`dsf.ports.GitHubClient` port.
        """
        token = self.installation_token()
        async with httpx.AsyncClient(transport=self.transport, base_url=_GITHUB_API) as client:
            resp = await client.post(
                f"/repos/{repo}/issues",
                headers=self._token_headers(token),
                json={"title": title, "body": body, "labels": labels},
            )
            resp.raise_for_status()
            data = resp.json()
        await self.assign_coding_agent(repo, data["node_id"])
        return data["html_url"]
