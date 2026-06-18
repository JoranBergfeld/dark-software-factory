"""Real GitHubClient that files issues via the ``gh`` CLI.

Calls ``gh issue create`` as a subprocess so no GitHub token needs to be
embedded in the service container — ``gh`` reads it from the environment or
its own credential store.  The ``_run`` constructor parameter lets tests inject
a mock so no real network call is made.
"""

from __future__ import annotations

import asyncio
import subprocess
from collections.abc import Callable
from typing import Any


class RealGitHubClient:
    """Files GitHub issues using the ``gh`` CLI.

    Parameters
    ----------
    gh_bin:
        Path (or name) of the ``gh`` binary.  Defaults to ``"gh"`` so it is
        resolved from ``PATH``.
    _run:
        Optional callable with the same signature as :func:`subprocess.run`.
        Inject a mock in tests to avoid real network calls.
    """

    def __init__(
        self,
        gh_bin: str = "gh",
        _run: Callable[..., Any] | None = None,
    ) -> None:
        self._gh = gh_bin
        self._subprocess_run = _run or subprocess.run

    async def create_issue(
        self,
        repo: str,
        title: str,
        body: str,
        labels: list[str],
    ) -> str:
        """Create a GitHub issue and return its URL.

        Raises :class:`subprocess.CalledProcessError` if ``gh`` exits non-zero.
        """
        cmd = [
            self._gh,
            "issue", "create",
            "--repo", repo,
            "--title", title,
            "--body", body,
        ]
        for label in labels:
            cmd.extend(["--label", label])

        run_fn = self._subprocess_run
        loop = asyncio.get_running_loop()
        result = await loop.run_in_executor(
            None,
            lambda: run_fn(cmd, capture_output=True, text=True, check=True),
        )
        return result.stdout.strip()


class RecordingGitHubClient:
    """In-memory GitHub client for local/offline mode.

    Records issue-creation calls in ``self.calls`` and returns deterministic
    ``local://issue/<n>`` URLs. Never touches the network — used by local mode and
    as a recording double in tests.
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


__all__ = ["RealGitHubClient", "RecordingGitHubClient"]
