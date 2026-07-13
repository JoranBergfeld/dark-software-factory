from __future__ import annotations

import base64
import json
from datetime import UTC, datetime

import httpx
import jwt
import pytest
from cryptography.hazmat.primitives import serialization
from cryptography.hazmat.primitives.asymmetric import rsa

from dsf.github_app_client import GitHubAppClient


def _rsa_pem() -> tuple[str, object]:
    key = rsa.generate_private_key(public_exponent=65537, key_size=2048)
    pem = key.private_bytes(
        encoding=serialization.Encoding.PEM,
        format=serialization.PrivateFormat.PKCS8,
        encryption_algorithm=serialization.NoEncryption(),
    ).decode()
    return pem, key.public_key()


def _fixed_clock(moment: datetime):
    return lambda: moment


def _token_handler(extra):
    """MockTransport handler: serves the token mint + delegates other paths to ``extra``."""

    def handler(request: httpx.Request) -> httpx.Response:
        if request.url.path.endswith("/access_tokens"):
            return httpx.Response(
                201, json={"token": "ghs_x", "expires_at": "2026-06-22T13:00:00Z"}
            )
        return extra(request)

    return handler


def _app_client(handler):
    pem, _ = _rsa_pem()
    return GitHubAppClient(
        app_id="1",
        installation_id="2",
        private_key_pem=pem,
        transport=httpx.MockTransport(handler),
        clock=_fixed_clock(datetime(2026, 6, 22, 12, 0, tzinfo=UTC)),
    )


def test_repr_does_not_leak_private_key():
    pem, _ = _rsa_pem()
    client = GitHubAppClient(app_id="1", installation_id="2", private_key_pem=pem)
    assert "BEGIN PRIVATE KEY" not in repr(client)
    assert pem not in repr(client)


def test_installation_token_mints_repo_scoped_token_and_signs_valid_jwt():
    pem, public_key = _rsa_pem()
    now = datetime(2026, 6, 22, 12, 0, tzinfo=UTC)
    seen: dict[str, object] = {}

    def handler(request: httpx.Request) -> httpx.Response:
        seen["url"] = str(request.url)
        seen["auth"] = request.headers["Authorization"]
        seen["body"] = request.read()
        token = request.headers["Authorization"].removeprefix("Bearer ")
        claims = jwt.decode(
            token,
            public_key,
            algorithms=["RS256"],
            options={"verify_exp": False},
        )
        seen["iss"] = claims["iss"]
        return httpx.Response(
            201,
            json={"token": "ghs_installtoken", "expires_at": "2026-06-22T13:00:00Z"},
        )

    client = GitHubAppClient(
        app_id="123",
        installation_id="456",
        private_key_pem=pem,
        repository_ids=[789],
        transport=httpx.MockTransport(handler),
        clock=_fixed_clock(now),
    )

    assert client.installation_token() == "ghs_installtoken"
    assert seen["url"] == "https://api.github.com/app/installations/456/access_tokens"
    assert seen["iss"] == "123"
    assert b'"repository_ids"' in seen["body"] and b"789" in seen["body"]


def test_installation_token_scopes_by_repository_name():
    pem, _ = _rsa_pem()
    seen: dict[str, object] = {}

    def handler(request: httpx.Request) -> httpx.Response:
        seen["body"] = request.read()
        return httpx.Response(
            201,
            json={"token": "ghs_x", "expires_at": "2026-06-22T13:00:00Z"},
        )

    client = GitHubAppClient(
        app_id="1",
        installation_id="2",
        private_key_pem=pem,
        repositories=["demo"],
        transport=httpx.MockTransport(handler),
        clock=_fixed_clock(datetime(2026, 6, 22, 12, 0, tzinfo=UTC)),
    )
    client.installation_token()

    assert json.loads(seen["body"]) == {"repositories": ["demo"]}


def test_installation_token_caches_until_near_expiry_then_refreshes():
    pem, _ = _rsa_pem()
    now = {"t": datetime(2026, 6, 22, 12, 0, tzinfo=UTC)}
    calls = {"n": 0}

    def handler(request: httpx.Request) -> httpx.Response:
        calls["n"] += 1
        return httpx.Response(
            201,
            json={"token": f"ghs_{calls['n']}", "expires_at": "2026-06-22T13:00:00Z"},
        )

    client = GitHubAppClient(
        app_id="1",
        installation_id="2",
        private_key_pem=pem,
        transport=httpx.MockTransport(handler),
        clock=lambda: now["t"],
    )

    assert client.installation_token() == "ghs_1"
    assert client.installation_token() == "ghs_1"  # cached, no second call
    assert calls["n"] == 1

    now["t"] = datetime(2026, 6, 22, 12, 59, 30, tzinfo=UTC)  # inside skew
    assert client.installation_token() == "ghs_2"
    assert calls["n"] == 2


def test_installation_token_raises_on_api_error():
    pem, _ = _rsa_pem()
    client = GitHubAppClient(
        app_id="1",
        installation_id="2",
        private_key_pem=pem,
        transport=httpx.MockTransport(lambda r: httpx.Response(404, json={})),
        clock=lambda: datetime(2026, 6, 22, tzinfo=UTC),
    )
    with pytest.raises(httpx.HTTPStatusError):
        client.installation_token()


def test_has_repository_access_true_when_app_installed_on_repo():
    def handler(request: httpx.Request) -> httpx.Response:
        if request.url.path == "/repos/acme/demo/installation":
            return httpx.Response(200, json={"id": 142291975, "repository_selection": "all"})
        return httpx.Response(404, json={"message": "Not Found"})

    client = _app_client(handler)

    assert client.has_repository_access("acme/demo") is True


def test_has_repository_access_false_when_repo_not_covered():
    def handler(request: httpx.Request) -> httpx.Response:
        if request.url.path == "/repos/acme/demo/installation":
            return httpx.Response(404, json={"message": "Not Found"})
        return httpx.Response(500, json={"message": "unexpected path"})

    client = _app_client(handler)

    assert client.has_repository_access("acme/demo") is False


async def test_assign_coding_agent_replaces_actors_with_copilot_bot():
    calls: list[dict] = []

    def extra(request: httpx.Request) -> httpx.Response:
        payload = json.loads(request.read())
        calls.append(payload)
        if "suggestedActors" in payload["query"]:
            return httpx.Response(
                200,
                json={
                    "data": {
                        "repository": {
                            "suggestedActors": {
                                "nodes": [
                                    {"login": "someone-else", "__typename": "User", "id": "U_1"},
                                    {
                                        "login": "copilot-swe-agent",
                                        "__typename": "Bot",
                                        "id": "BOT_42",
                                    },
                                ]
                            }
                        }
                    }
                },
            )
        return httpx.Response(
            200, json={"data": {"replaceActorsForAssignable": {"assignable": {"id": "ISSUE_1"}}}}
        )

    client = _app_client(_token_handler(extra))
    await client.assign_coding_agent("acme/demo", "ISSUE_1")

    assert calls[0]["variables"] == {"owner": "acme", "name": "demo"}
    assert calls[1]["variables"] == {"assignableId": "ISSUE_1", "actorIds": ["BOT_42"]}


async def test_create_issue_files_then_assigns_and_returns_url():
    seen: dict[str, object] = {}

    def extra(request: httpx.Request) -> httpx.Response:
        if request.url.path == "/repos/acme/demo/issues":
            seen["issue_body"] = json.loads(request.read())
            return httpx.Response(
                201,
                json={
                    "html_url": "https://github.com/acme/demo/issues/7",
                    "node_id": "ISSUE_NODE_7",
                },
            )
        payload = json.loads(request.read())
        if "suggestedActors" in payload["query"]:
            return httpx.Response(
                200,
                json={
                    "data": {
                        "repository": {
                            "suggestedActors": {
                                "nodes": [
                                    {
                                        "login": "copilot-swe-agent",
                                        "__typename": "Bot",
                                        "id": "BOT_42",
                                    },
                                ]
                            }
                        }
                    }
                },
            )
        seen["assign_vars"] = payload["variables"]
        return httpx.Response(
            200,
            json={"data": {"replaceActorsForAssignable": {"assignable": {"id": "ISSUE_NODE_7"}}}},
        )

    client = _app_client(_token_handler(extra))
    url = await client.create_issue("acme/demo", "Title", "Body", ["enhancement"])

    assert url == "https://github.com/acme/demo/issues/7"
    assert seen["issue_body"] == {"title": "Title", "body": "Body", "labels": ["enhancement"]}
    assert seen["assign_vars"] == {"assignableId": "ISSUE_NODE_7", "actorIds": ["BOT_42"]}


async def test_assign_coding_agent_raises_when_copilot_not_assignable():
    def extra(request: httpx.Request) -> httpx.Response:
        return httpx.Response(
            200,
            json={
                "data": {
                    "repository": {
                        "suggestedActors": {
                            "nodes": [
                                {"login": "someone-else", "__typename": "User", "id": "U_1"},
                            ]
                        }
                    }
                }
            },
        )

    client = _app_client(_token_handler(extra))
    with pytest.raises(RuntimeError, match="copilot-swe-agent"):
        await client.assign_coding_agent("acme/demo", "ISSUE_1")


async def test_create_issue_raises_assignment_error_carrying_url_when_assign_fails():
    from dsf.ports import CodingAgentAssignmentError

    def extra(request: httpx.Request) -> httpx.Response:
        if request.url.path == "/repos/acme/demo/issues":
            return httpx.Response(
                201,
                json={
                    "html_url": "https://github.com/acme/demo/issues/9",
                    "node_id": "ISSUE_NODE_9",
                },
            )
        # suggestedActors with no copilot bot -> assign_coding_agent raises
        return httpx.Response(
            200,
            json={
                "data": {
                    "repository": {
                        "suggestedActors": {
                            "nodes": [
                                {"login": "someone-else", "__typename": "User", "id": "U_1"},
                            ]
                        }
                    }
                }
            },
        )

    client = _app_client(_token_handler(extra))
    with pytest.raises(CodingAgentAssignmentError) as excinfo:
        await client.create_issue("acme/demo", "Title", "Body", ["enhancement"])
    assert excinfo.value.issue_url == "https://github.com/acme/demo/issues/9"
    assert excinfo.value.issue_node_id == "ISSUE_NODE_9"


async def test_read_file_decodes_base64_text():
    def extra(request: httpx.Request) -> httpx.Response:
        assert request.url.path == "/repos/org/alpha/contents/.dsf/charter.md"
        assert request.url.params["ref"] == "main"
        return httpx.Response(
            200,
            json={"content": base64.b64encode(b"hello charter").decode(), "sha": "deadbeef"},
        )

    client = _app_client(_token_handler(extra))
    fc = await client.read_file("org/alpha", ".dsf/charter.md")
    assert fc is not None
    assert fc.text == "hello charter"
    assert fc.sha == "deadbeef"
    assert fc.ref == "main"


async def test_read_file_returns_none_on_404():
    def extra(request: httpx.Request) -> httpx.Response:
        return httpx.Response(404, json={"message": "Not Found"})

    client = _app_client(_token_handler(extra))
    assert await client.read_file("org/alpha", ".dsf/charter.md") is None


async def test_open_file_pr_creates_branch_writes_file_and_opens_pr():
    seen: list[tuple[str, str]] = []

    def extra(request: httpx.Request) -> httpx.Response:
        method, path = request.method, request.url.path
        seen.append((method, path))
        if method == "GET" and path.endswith("/git/ref/heads/main"):
            return httpx.Response(200, json={"object": {"sha": "basesha"}})
        if method == "POST" and path.endswith("/git/refs"):
            body = json.loads(request.read())
            assert body == {"ref": "refs/heads/charter/alpha", "sha": "basesha"}
            return httpx.Response(201, json={})
        if method == "GET" and "/contents/" in path:
            return httpx.Response(404, json={})  # new file (no existing sha)
        if method == "PUT" and "/contents/" in path:
            body = json.loads(request.read())
            assert body["branch"] == "charter/alpha"
            assert base64.b64decode(body["content"]).decode() == "BODY"
            assert "sha" not in body
            return httpx.Response(201, json={})
        if method == "POST" and path.endswith("/pulls"):
            body = json.loads(request.read())
            assert body == {
                "title": "T",
                "body": "B",
                "head": "charter/alpha",
                "base": "main",
            }
            return httpx.Response(201, json={"html_url": "https://github.com/org/alpha/pull/1"})
        return httpx.Response(500, json={"unexpected": path})

    client = _app_client(_token_handler(extra))
    url = await client.open_file_pr(
        "org/alpha",
        path=".dsf/charter.md",
        content="BODY",
        branch="charter/alpha",
        title="T",
        body="B",
        message="add charter",
    )
    assert url == "https://github.com/org/alpha/pull/1"
    assert ("PUT", "/repos/org/alpha/contents/.dsf/charter.md") in seen


async def test_open_file_pr_applies_labels_when_given():
    seen: list[tuple[str, str]] = []
    label_body: dict[str, object] = {}

    def extra(request: httpx.Request) -> httpx.Response:
        method, path = request.method, request.url.path
        seen.append((method, path))
        if method == "GET" and path.endswith("/git/ref/heads/main"):
            return httpx.Response(200, json={"object": {"sha": "basesha"}})
        if method == "POST" and path.endswith("/git/refs"):
            return httpx.Response(201, json={})
        if method == "GET" and "/contents/" in path:
            return httpx.Response(404, json={})
        if method == "PUT" and "/contents/" in path:
            return httpx.Response(201, json={})
        if method == "POST" and path.endswith("/pulls"):
            return httpx.Response(
                201,
                json={"html_url": "https://github.com/org/alpha/pull/5", "number": 5},
            )
        if method == "POST" and path.endswith("/issues/5/labels"):
            label_body.update(json.loads(request.read()))
            return httpx.Response(200, json=[])
        return httpx.Response(500, json={"unexpected": path})

    client = _app_client(_token_handler(extra))
    url = await client.open_file_pr(
        "org/alpha",
        path=".dsf/charter.md",
        content="BODY",
        branch="charter/amend/abc",
        title="T",
        body="B",
        message="propose amendment",
        labels=["governance", "charter-amendment"],
    )
    assert url == "https://github.com/org/alpha/pull/5"
    assert label_body == {"labels": ["governance", "charter-amendment"]}
    assert ("POST", "/repos/org/alpha/issues/5/labels") in seen


async def test_open_file_pr_enables_auto_merge_when_requested():
    graphql_bodies: list[dict] = []

    def extra(request: httpx.Request) -> httpx.Response:
        method, path = request.method, request.url.path
        if method == "GET" and path.endswith("/git/ref/heads/main"):
            return httpx.Response(200, json={"object": {"sha": "basesha"}})
        if method == "POST" and path.endswith("/git/refs"):
            return httpx.Response(201, json={})
        if method == "GET" and "/contents/" in path:
            return httpx.Response(404, json={})
        if method == "PUT" and "/contents/" in path:
            return httpx.Response(201, json={})
        if method == "POST" and path.endswith("/pulls"):
            return httpx.Response(
                201,
                json={
                    "html_url": "https://github.com/org/alpha/pull/7",
                    "number": 7,
                    "node_id": "PR_kw7",
                },
            )
        if method == "POST" and path == "/graphql":
            graphql_bodies.append(json.loads(request.read()))
            return httpx.Response(
                200,
                json={
                    "data": {
                        "enablePullRequestAutoMerge": {
                            "pullRequest": {
                                "autoMergeRequest": {
                                    "enabledAt": "now",
                                }
                            }
                        }
                    }
                },
            )
        return httpx.Response(500, json={"unexpected": path})

    client = _app_client(_token_handler(extra))
    url = await client.open_file_pr(
        "org/alpha",
        path=".specify/memory/constitution.md",
        content="C",
        branch="charter/constitution/x",
        title="T",
        body="B",
        message="m",
        enable_auto_merge=True,
    )
    assert url == "https://github.com/org/alpha/pull/7"
    assert graphql_bodies and "enablePullRequestAutoMerge" in graphql_bodies[0]["query"]
    assert graphql_bodies[0]["variables"] == {"pullRequestId": "PR_kw7"}


async def test_open_file_pr_auto_merge_degrades_when_not_allowed():
    def extra(request: httpx.Request) -> httpx.Response:
        method, path = request.method, request.url.path
        if method == "GET" and path.endswith("/git/ref/heads/main"):
            return httpx.Response(200, json={"object": {"sha": "basesha"}})
        if method == "POST" and path.endswith("/git/refs"):
            return httpx.Response(201, json={})
        if method == "GET" and "/contents/" in path:
            return httpx.Response(404, json={})
        if method == "PUT" and "/contents/" in path:
            return httpx.Response(201, json={})
        if method == "POST" and path.endswith("/pulls"):
            return httpx.Response(
                201,
                json={
                    "html_url": "https://github.com/org/alpha/pull/8",
                    "number": 8,
                    "node_id": "PR_x",
                },
            )
        if method == "POST" and path == "/graphql":
            return httpx.Response(
                200,
                json={
                    "errors": [
                        {
                            "message": (
                                "Pull request Auto merge is not allowed for this repository"
                            )
                        }
                    ]
                },
            )
        return httpx.Response(500, json={"unexpected": path})

    client = _app_client(_token_handler(extra))
    url = await client.open_file_pr(
        "org/alpha",
        path=".specify/memory/constitution.md",
        content="C",
        branch="b",
        title="T",
        body="B",
        message="m",
        enable_auto_merge=True,
    )
    # The GraphQL error is swallowed; the PR is still reported (a human will merge it).
    assert url == "https://github.com/org/alpha/pull/8"


async def test_open_file_pr_auto_merge_degrades_on_http_error():
    def extra(request: httpx.Request) -> httpx.Response:
        method, path = request.method, request.url.path
        if method == "GET" and path.endswith("/git/ref/heads/main"):
            return httpx.Response(200, json={"object": {"sha": "basesha"}})
        if method == "POST" and path.endswith("/git/refs"):
            return httpx.Response(201, json={})
        if method == "GET" and "/contents/" in path:
            return httpx.Response(404, json={})
        if method == "PUT" and "/contents/" in path:
            return httpx.Response(201, json={})
        if method == "POST" and path.endswith("/pulls"):
            return httpx.Response(
                201,
                json={
                    "html_url": "https://github.com/org/alpha/pull/9",
                    "number": 9,
                    "node_id": "PR_y",
                },
            )
        if method == "POST" and path == "/graphql":
            return httpx.Response(502, json={"message": "Bad Gateway"})
        return httpx.Response(500, json={"unexpected": path})

    client = _app_client(_token_handler(extra))
    url = await client.open_file_pr(
        "org/alpha",
        path=".specify/memory/constitution.md",
        content="C",
        branch="b",
        title="T",
        body="B",
        message="m",
        enable_auto_merge=True,
    )
    # A non-2xx GraphQL response raises HTTPStatusError, which is swallowed too.
    assert url == "https://github.com/org/alpha/pull/9"


async def test_open_file_pr_auto_merge_degrades_on_transport_error():
    def extra(request: httpx.Request) -> httpx.Response:
        method, path = request.method, request.url.path
        if method == "GET" and path.endswith("/git/ref/heads/main"):
            return httpx.Response(200, json={"object": {"sha": "basesha"}})
        if method == "POST" and path.endswith("/git/refs"):
            return httpx.Response(201, json={})
        if method == "GET" and "/contents/" in path:
            return httpx.Response(404, json={})
        if method == "PUT" and "/contents/" in path:
            return httpx.Response(201, json={})
        if method == "POST" and path.endswith("/pulls"):
            return httpx.Response(
                201,
                json={
                    "html_url": "https://github.com/org/alpha/pull/10",
                    "number": 10,
                    "node_id": "PR_z",
                },
            )
        if method == "POST" and path == "/graphql":
            raise httpx.ConnectError("connection reset", request=request)
        return httpx.Response(500, json={"unexpected": path})

    client = _app_client(_token_handler(extra))
    url = await client.open_file_pr(
        "org/alpha",
        path=".specify/memory/constitution.md",
        content="C",
        branch="b",
        title="T",
        body="B",
        message="m",
        enable_auto_merge=True,
    )
    # A transport fault (httpx.RequestError) on the trailing auto-merge call must
    # not abort open_file_pr after the PR already exists.
    assert url == "https://github.com/org/alpha/pull/10"


async def test_latest_pr_with_head_prefix_returns_first_match_newest_first():
    def extra(request: httpx.Request) -> httpx.Response:
        assert request.url.path == "/repos/org/alpha/pulls"
        assert request.url.params["state"] == "all"
        assert request.url.params["direction"] == "desc"
        return httpx.Response(
            200,
            json=[
                {
                    "html_url": "https://github.com/org/alpha/pull/12",
                    "state": "open",
                    "created_at": "2026-06-23T10:00:00Z",
                    "head": {"ref": "charter/amend/deadbeef"},
                },
                {
                    "html_url": "https://github.com/org/alpha/pull/3",
                    "state": "closed",
                    "created_at": "2026-06-01T10:00:00Z",
                    "head": {"ref": "feature/unrelated"},
                },
            ],
        )

    client = _app_client(_token_handler(extra))
    ref = await client.latest_pr_with_head_prefix("org/alpha", head_prefix="charter/amend/")
    assert ref is not None
    assert ref.html_url == "https://github.com/org/alpha/pull/12"
    assert ref.state == "open"
    assert ref.head_ref == "charter/amend/deadbeef"
    assert ref.created_at == datetime(2026, 6, 23, 10, 0, tzinfo=UTC)


async def test_latest_pr_with_head_prefix_returns_none_when_no_match():
    def extra(request: httpx.Request) -> httpx.Response:
        return httpx.Response(
            200,
            json=[
                {
                    "html_url": "https://github.com/org/alpha/pull/3",
                    "state": "closed",
                    "created_at": "2026-06-01T10:00:00Z",
                    "head": {"ref": "feature/unrelated"},
                }
            ],
        )

    client = _app_client(_token_handler(extra))
    assert (
        await client.latest_pr_with_head_prefix("org/alpha", head_prefix="charter/amend/")
        is None
    )
