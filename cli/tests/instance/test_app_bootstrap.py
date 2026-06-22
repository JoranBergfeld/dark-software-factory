from __future__ import annotations

from datetime import UTC, datetime

import httpx
import jwt
import pytest
from cryptography.hazmat.primitives import serialization
from cryptography.hazmat.primitives.asymmetric import rsa

from dsf.instance.app_bootstrap import (
    AppCredentials,
    app_manifest,
    discover_installation_id,
    exchange_manifest_code,
    owner_kv_ensure_commands,
    owner_kv_store_commands,
)


def _rsa_pem() -> str:
    key = rsa.generate_private_key(public_exponent=65537, key_size=2048)
    return key.private_bytes(
        encoding=serialization.Encoding.PEM,
        format=serialization.PrivateFormat.PKCS8,
        encryption_algorithm=serialization.NoEncryption(),
    ).decode()


def test_app_manifest_describes_least_privilege_app():
    manifest = app_manifest(name="dsf-acme", callback_url="http://127.0.0.1:8765/cb")
    assert manifest["name"] == "dsf-acme"
    assert manifest["url"] == "http://127.0.0.1:8765/cb"
    assert manifest["public"] is False
    assert manifest["default_permissions"] == {
        "issues": "write",
        "pull_requests": "write",
        "contents": "read",
        "administration": "write",
    }


def test_exchange_manifest_code_posts_to_conversions_and_parses_credentials():
    seen: dict[str, str] = {}

    def handler(request: httpx.Request) -> httpx.Response:
        seen["url"] = str(request.url)
        return httpx.Response(
            201,
            json={
                "id": 42,
                "slug": "dsf-acme",
                "pem": "-----BEGIN PRIVATE KEY-----\nx\n-----END PRIVATE KEY-----\n",
                "webhook_secret": "whs",
                "client_id": "cid",
                "client_secret": "csec",
            },
        )

    creds = exchange_manifest_code("tempcode", transport=httpx.MockTransport(handler))
    assert seen["url"] == "https://api.github.com/app-manifests/tempcode/conversions"
    assert creds == AppCredentials(
        app_id="42",
        slug="dsf-acme",
        pem="-----BEGIN PRIVATE KEY-----\nx\n-----END PRIVATE KEY-----\n",
        webhook_secret="whs",
        client_id="cid",
        client_secret="csec",
    )


def test_app_credentials_repr_hides_secrets():
    creds = AppCredentials(
        app_id="42",
        slug="s",
        pem="-----BEGIN PRIVATE KEY-----",
        webhook_secret="whs",
        client_id="cid",
        client_secret="csec",
    )
    text = repr(creds)
    assert "BEGIN PRIVATE KEY" not in text
    assert "whs" not in text
    assert "csec" not in text
    assert "42" in text


def test_discover_installation_id_polls_until_present():
    creds = AppCredentials(app_id="42", slug="s", pem=_rsa_pem(), webhook_secret="",
                           client_id="", client_secret="")
    responses = iter([
        httpx.Response(200, json=[]),
        httpx.Response(200, json=[{"id": 9001}]),
    ])

    def handler(request: httpx.Request) -> httpx.Response:
        assert request.headers["Authorization"].startswith("Bearer ")
        jwt.decode(
            request.headers["Authorization"].removeprefix("Bearer "),
            options={"verify_signature": False},
        )
        return next(responses)

    slept: list[float] = []
    got = discover_installation_id(
        creds,
        transport=httpx.MockTransport(handler),
        clock=lambda: datetime(2026, 6, 22, tzinfo=UTC),
        sleep=slept.append,
        max_attempts=5,
    )
    assert got == "9001"
    assert slept == [pytest.approx(5.0)]


def test_owner_kv_ensure_commands_create_rbac_vault_and_grant_operator():
    cmds = owner_kv_ensure_commands(
        resource_group="rg-dsf-app",
        keyvault_name="kv-dsf-app",
        location="swedencentral",
        operator_object_id="oid-1",
    )
    assert ["az", "group", "create", "--name", "rg-dsf-app",
            "--location", "swedencentral"] in cmds
    create = next(c for c in cmds if c[:3] == ["az", "keyvault", "create"])
    assert "--enable-rbac-authorization" in create
    grant = next(c for c in cmds if c[:4] == ["az", "role", "assignment", "create"])
    assert "Key Vault Secrets Officer" in grant
    assert "oid-1" in grant


def test_owner_kv_store_commands_set_id_installation_and_keyfile():
    cmds = owner_kv_store_commands(
        keyvault_name="kv-dsf-app",
        app_id="42",
        installation_id="9001",
        pem_path="/tmp/app.pem",
    )
    assert ["az", "keyvault", "secret", "set", "--vault-name", "kv-dsf-app",
            "--name", "github-app-id", "--value", "42"] in cmds
    assert ["az", "keyvault", "secret", "set", "--vault-name", "kv-dsf-app",
            "--name", "github-app-installation-id", "--value", "9001"] in cmds
    keyset = next(c for c in cmds if "github-app-private-key" in c)
    assert "--file" in keyset and "/tmp/app.pem" in keyset
    assert "--value" not in keyset
