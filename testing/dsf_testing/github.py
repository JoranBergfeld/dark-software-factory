"""Recording GitHubClient double for offline tests.

Records issue-creation calls and returns deterministic ``local://issue/<n>``
URLs without touching the network.
"""

from __future__ import annotations

from typing import NamedTuple


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


class _RepoFile(NamedTuple):
    text: str
    sha: str
    ref: str


class RecordingRepoClient:
    """Double for the App's file surface: ``read_file`` / ``open_file_pr``.

    ``files`` maps ``path -> (text, blob_sha)``. ``open_file_pr`` records each PR
    and returns a deterministic URL. Mirrors ``dsf.github_app_client`` duck-typed
    (returns objects with ``.text``/``.sha``/``.ref``) without importing core.
    """

    def __init__(
        self,
        files: dict[str, tuple[str, str]] | None = None,
        *,
        repositories: list[str] | None = None,
    ) -> None:
        self._files = dict(files or {})
        self.repositories = repositories
        self.prs: list[dict] = []

    async def read_file(self, repo: str, path: str, ref: str = "main") -> _RepoFile | None:
        if path not in self._files:
            return None
        text, sha = self._files[path]
        return _RepoFile(text=text, sha=sha, ref=ref)

    async def open_file_pr(
        self,
        repo: str,
        *,
        path: str,
        content: str,
        branch: str,
        base: str = "main",
        title: str,
        body: str,
        message: str,
    ) -> str:
        self.prs.append(
            {
                "repo": repo,
                "path": path,
                "content": content,
                "branch": branch,
                "base": base,
                "title": title,
                "body": body,
                "message": message,
            }
        )
        return f"https://github.com/{repo}/pull/{len(self.prs)}"


__all__ = ["RecordingGitHubClient", "RecordingRepoClient"]
