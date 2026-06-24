from __future__ import annotations

import subprocess
from datetime import UTC, datetime

import httpx
import jwt
import pytest
from cryptography.hazmat.primitives import serialization
from cryptography.hazmat.primitives.asymmetric import rsa

from dsf.instance.app_bootstrap import (
    AppCredentials,
    BootstrapConfig,
    app_manifest,
    bootstrap_app,
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
        "contents": "write",
        "administration": "write",
    }
    # No webhook events: GitHub rejects a manifest that declares events without a
    # hook URL, and the App needs none (installation-token REST/GraphQL only).
    assert "default_events" not in manifest
    assert "hook_attributes" not in manifest


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
    assert "--enable-purge-protection" in create
    assert "--retention-days" in create
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
            "--name", "github-app-id", "--value", "42", "-o", "none"] in cmds
    assert ["az", "keyvault", "secret", "set", "--vault-name", "kv-dsf-app",
            "--name", "github-app-installation-id", "--value", "9001", "-o", "none"] in cmds
    keyset = next(c for c in cmds if "github-app-private-key" in c)
    assert "--file" in keyset and "/tmp/app.pem" in keyset
    assert "--value" not in keyset
    # The private-key write must not echo the stored secret bundle to stdout.
    assert keyset[-2:] == ["-o", "none"]


def test_bootstrap_app_runs_ensure_then_store_with_resolved_scope(tmp_path):
    creds = AppCredentials(
        app_id="42",
        slug="dsf-acme",
        pem="PEMDATA",
        webhook_secret="",
        client_id="",
        client_secret="",
    )
    calls: list[list[str]] = []

    def fake_run(cmd, **kwargs):
        calls.append(cmd)

        class R:
            stdout = ""
            returncode = 0

        if cmd[:3] == ["az", "account", "show"]:
            R.stdout = "sub-123\n"
        if cmd[:4] == ["az", "ad", "signed-in-user", "show"]:
            R.stdout = "oid-1\n"
        return R()

    cfg = BootstrapConfig(
        app_name="dsf-acme",
        resource_group="rg-dsf-app",
        keyvault_name="kv-dsf-app",
        location="swedencentral",
    )
    result = bootstrap_app(
        cfg,
        run=fake_run,
        capture_code=lambda manifest: "tempcode",
        exchange=lambda code, **_: creds,
        discover=lambda c, **_: "9001",
        write_pem=lambda pem: str(tmp_path / "app.pem"),
    )

    assert result.app_id == "42"
    assert result.installation_id == "9001"
    assert "kv-dsf-app" in result.keyvault_uri
    # Secrets Officer scope had its subscription id substituted before running.
    grant = next(c for c in calls if c[:4] == ["az", "role", "assignment", "create"])
    assert "/subscriptions/sub-123/resourceGroups/rg-dsf-app/" in " ".join(grant)
    # The private key was stored from a file, not as a CLI value.
    keyset = next(c for c in calls if "github-app-private-key" in c)
    assert "--file" in keyset and "--value" not in keyset


def test_bootstrap_app_retries_secret_writes_on_rbac_propagation(tmp_path):
    creds = AppCredentials(app_id="42", slug="s", pem="PEMDATA", webhook_secret="",
                           client_id="", client_secret="")
    failures = {"left": 2}

    def fake_run(cmd, **kwargs):
        class R:
            stdout = ""
            returncode = 0

        if cmd[:3] == ["az", "account", "show"]:
            R.stdout = "sub-123\n"
        if cmd[:4] == ["az", "ad", "signed-in-user", "show"]:
            R.stdout = "oid-1\n"
        # The data-plane secret writes 403 until the Secrets Officer grant propagates.
        if "github-app-private-key" in cmd and failures["left"] > 0:
            failures["left"] -= 1
            raise subprocess.CalledProcessError(1, cmd)
        return R()

    slept: list[float] = []
    result = bootstrap_app(
        BootstrapConfig(app_name="s", resource_group="rg", keyvault_name="kv"),
        run=fake_run,
        capture_code=lambda manifest: "tempcode",
        exchange=lambda code, **_: creds,
        discover=lambda c, **_: "9001",
        write_pem=lambda pem: str(tmp_path / "app.pem"),
        sleep=slept.append,
    )
    assert result.installation_id == "9001"
    assert failures["left"] == 0  # both 403s were absorbed
    assert len(slept) == 2  # backed off once per failed attempt
