from __future__ import annotations

import asyncio

from dsf.ports import CodingAgentAssignmentError
from dsf_testing.github import RecordingRepoClient


def test_create_issue_records_and_returns_url():
    client = RecordingRepoClient({})
    url = asyncio.run(client.create_issue("org/alpha", "T", "B", ["creation:ready"]))
    assert url == "local://issue/1"
    assert client.issues == [
        {"repo": "org/alpha", "title": "T", "body": "B", "labels": ["creation:ready"]}
    ]


def test_create_issue_raises_supplied_error():
    boom = CodingAgentAssignmentError(
        "no copilot", issue_url="local://issue/1", issue_node_id="N"
    )
    client = RecordingRepoClient({}, create_issue_error=boom)
    try:
        asyncio.run(client.create_issue("org/alpha", "T", "B", []))
        raise AssertionError("expected CodingAgentAssignmentError")
    except CodingAgentAssignmentError as exc:
        assert exc.issue_url == "local://issue/1"


def test_open_file_pr_records_auto_merge_flag():
    client = RecordingRepoClient({})
    asyncio.run(
        client.open_file_pr(
            "org/alpha",
            path="p",
            content="x",
            branch="b",
            title="T",
            body="B",
            message="m",
            enable_auto_merge=True,
        )
    )
    assert client.prs[0]["enable_auto_merge"] is True
