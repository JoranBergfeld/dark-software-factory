"""Recording GitHubClient double for offline tests.

Records issue-creation calls and returns deterministic ``local://issue/<n>``
URLs without touching the network.
"""

from __future__ import annotations


class RecordingGitHubClient:
    """In-memory GitHub client for offline tests.

    Records issue-creation calls in ``self.calls`` and returns deterministic
    ``local://issue/<n>`` URLs. Never touches the network.
    """

    def __init__(self) -> None:
        self.calls: list[dict] = []

    async def create_issue(
        self,
        repo: str,
        title: str,
        body: str,
        labels: list[str],
    ) -> str:
        """Record the call and return a deterministic local URL."""
        self.calls.append(
            {"repo": repo, "title": title, "body": body, "labels": list(labels)}
        )
        return f"local://issue/{len(self.calls)}"


__all__ = ["RecordingGitHubClient"]
