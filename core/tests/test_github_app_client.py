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
