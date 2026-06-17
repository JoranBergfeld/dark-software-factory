"""Integration tests for RealGitHubClient with a mocked gh CLI."""

from __future__ import annotations

import subprocess
from unittest.mock import MagicMock

import pytest

from dsf.github_client import RealGitHubClient
from dsf.ports import GitHubClient


def _make_completed_process(stdout: str = "https://github.com/o/r/issues/42") -> MagicMock:
    result = MagicMock(spec=subprocess.CompletedProcess)
    result.stdout = stdout
    return result


def test_real_github_client_satisfies_protocol():
    assert isinstance(RealGitHubClient(), GitHubClient)


async def test_create_issue_calls_gh_cli():
    """create_issue invokes gh with the correct arguments and returns the URL."""
    calls: list[list[str]] = []

    def fake_run(cmd, **kwargs):
        calls.append(cmd)
        return _make_completed_process("https://github.com/o/r/issues/1\n")

    client = RealGitHubClient(_run=fake_run)
    url = await client.create_issue(
        repo="owner/repo",
        title="Test issue",
        body="Body text",
        labels=["bug", "triage"],
    )

    assert url == "https://github.com/o/r/issues/1"
    assert len(calls) == 1
    cmd = calls[0]
    assert "gh" in cmd[0]
    assert "issue" in cmd
    assert "create" in cmd
    assert "--repo" in cmd
    assert "owner/repo" in cmd
    assert "--title" in cmd
    assert "Test issue" in cmd
    assert "--label" in cmd
    assert "bug" in cmd
    assert "triage" in cmd


async def test_create_issue_no_labels():
    """create_issue works without labels."""
    calls: list[list[str]] = []

    def fake_run(cmd, **kwargs):
        calls.append(cmd)
        return _make_completed_process("https://github.com/o/r/issues/2")

    client = RealGitHubClient(_run=fake_run)
    url = await client.create_issue("o/r", "title", "body", [])

    assert url == "https://github.com/o/r/issues/2"
    assert "--label" not in calls[0]


async def test_create_issue_propagates_subprocess_error():
    """CalledProcessError from gh is propagated to the caller."""
    def fail_run(cmd, **kwargs):
        raise subprocess.CalledProcessError(1, cmd, stderr="not found")

    client = RealGitHubClient(_run=fail_run)
    with pytest.raises(subprocess.CalledProcessError):
        await client.create_issue("o/r", "t", "b", [])
