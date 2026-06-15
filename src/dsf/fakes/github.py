"""Deterministic fake GitHubClient that never touches the network."""

from __future__ import annotations


class FakeGitHubClient:
    """Records issue-creation calls and returns ``local://issue/<n>`` URLs."""

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
