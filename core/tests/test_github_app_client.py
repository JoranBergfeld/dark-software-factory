from __future__ import annotations

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
    import json

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


async def test_assign_coding_agent_replaces_actors_with_copilot_bot():
    import json

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
    import json

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
