"""Live GitHub client for :class:`IncidentsGitHubBackend`.

Builds an async ``gh_call(scope)`` callable that lists the product repository's
open issues carrying :data:`~dsf.contracts.handoff.INCIDENT_LABEL` via the GitHub
REST API over ``httpx``. Constructed from environment variables but accepts an
injected ``httpx.AsyncClient`` so tests can drive it with ``httpx.MockTransport``
and never touch the network.
"""

from __future__ import annotations

from typing import TYPE_CHECKING

import httpx

from dsf.agents.mode import env_required
from dsf.contracts.handoff import INCIDENT_LABEL

if TYPE_CHECKING:
    from collections.abc import Awaitable, Callable


def build_incidents_client_from_env(
    client: httpx.AsyncClient | None = None,
) -> Callable[[dict], Awaitable[list[dict]]]:
    """Return an async ``gh_call(scope)`` backed by the GitHub issues API.

    Env vars:

    * ``GITHUB_TOKEN`` (required) — token with read access to the repo's issues.
    * ``GITHUB_REPO`` (required) — ``owner/name`` of the product repository.

    The ``scope`` may override the repo via ``scope["github_repo"]``.
    """
    token = env_required("GITHUB_TOKEN", hint="GitHub token for incident issues")
    default_repo = env_required("GITHUB_REPO", hint="owner/name of product repo")
    auth_header = f"Bearer {token}"

    if client is None:
        client = httpx.AsyncClient(
            base_url="https://api.github.com",
            headers={
                "Authorization": auth_header,
                "Accept": "application/vnd.github+json",
            },
            timeout=20.0,
        )

    async def gh_call(scope: dict) -> list[dict]:
        repo = (scope or {}).get("github_repo") or default_repo
        resp = await client.get(
            f"/repos/{repo}/issues",
            params={"labels": INCIDENT_LABEL, "state": "open", "per_page": 100},
            headers={"Authorization": auth_header},
        )
        resp.raise_for_status()
        issues = resp.json() or []
        return [
            {
                "title": issue.get("title", ""),
                "html_url": issue.get("html_url", ""),
                "number": issue.get("number"),
            }
            for issue in issues
            # GitHub returns PRs on the issues endpoint too; drop them.
            if "pull_request" not in issue
        ]

    return gh_call


__all__ = ["build_incidents_client_from_env"]
