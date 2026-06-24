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


class _AmendmentPr(NamedTuple):
    html_url: str
    state: str
    created_at: object  # datetime; kept loose to avoid importing it here
    head_ref: str


class RecordingRepoClient:
    """Double for the App's file surface: ``read_file`` / ``open_file_pr``.

    ``files`` maps ``path -> (text, blob_sha)``. ``open_file_pr`` records each PR
    (including any ``labels``) and returns a deterministic URL. ``prs`` seeds
    pre-existing PRs that ``latest_pr_with_head_prefix`` reports (newest-first by
    ``created_at``), so the one-open-PR / cooldown guardrails are testable.
    Mirrors ``dsf.github_app_client`` duck-typed (returns objects with
    ``.text``/``.sha``/``.ref`` and ``.html_url``/``.state``/``.created_at``/
    ``.head_ref``) without importing core.
    """

    def __init__(
        self,
        files: dict[str, tuple[str, str]] | None = None,
        *,
        repositories: list[str] | None = None,
        prs: list[_AmendmentPr] | None = None,
    ) -> None:
        self._files = dict(files or {})
        self.repositories = repositories
        self._seed_prs = list(prs or [])
        self.prs: list[dict] = []

    async def read_file(self, repo: str, path: str, ref: str = "main") -> _RepoFile | None:
        if path not in self._files:
            return None
        text, sha = self._files[path]
        return _RepoFile(text=text, sha=sha, ref=ref)

    async def latest_pr_with_head_prefix(
        self, repo: str, *, head_prefix: str
    ) -> _AmendmentPr | None:
        matches = [pr for pr in self._seed_prs if pr.head_ref.startswith(head_prefix)]
        if not matches:
            return None
        return max(matches, key=lambda pr: pr.created_at)

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
        labels: list[str] | None = None,
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
                "labels": list(labels or []),
            }
        )
        return f"https://github.com/{repo}/pull/{len(self.prs)}"


__all__ = ["RecordingGitHubClient", "RecordingRepoClient", "SeedPr"]


#: Public alias for seeding pre-existing PRs into ``RecordingRepoClient``.
SeedPr = _AmendmentPr
