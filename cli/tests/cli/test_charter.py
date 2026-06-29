from __future__ import annotations

import asyncio
from types import SimpleNamespace

from dsf.charter.markdown import git_blob_sha, render_charter
from dsf.charter.sync import CHARTER_PATH
from dsf.cli.factory import build_parser, main
from dsf.contracts.charter import Charter, StoredCharter
from dsf.contracts.enums import CharterStatus
from dsf_testing.charter import InMemoryCharterStore
from dsf_testing.config import InMemoryConfigStore
from dsf_testing.github import RecordingRepoClient
from dsf_testing.model import DeterministicModelClient


def _ok_charter(source_sha: str = "abc123") -> Charter:
    return Charter(
        product="alpha",
        vision="V",
        target_users="U",
        goals=["g"],
        success_metrics=["m"],
        source_sha=source_sha,
        source_ref="main",
    )


def _put(store: InMemoryCharterStore, charter: Charter, status: CharterStatus) -> None:
    asyncio.run(
        store.put_charter(StoredCharter(product="alpha", charter=charter, status=status))
    )


def _fake_manifest(outputs):
    return SimpleNamespace(azure=SimpleNamespace(outputs=outputs))


def test_charter_parser_wires_all_subcommands():
    parser = build_parser()
    assert parser.parse_args(["charter", "status", "--product", "alpha"]).product == "alpha"
    assert (
        parser.parse_args(["charter", "sync", "--product", "alpha", "--ref", "main"]).ref
        == "main"
    )
    assert (
        parser.parse_args(["charter", "sync", "--product", "alpha", "--file", "x.md"]).file
        == "x.md"
    )
    init_args = parser.parse_args(["charter", "init", "--product", "alpha"])
    assert init_args.command == "charter" and init_args.product == "alpha"


def test_status_ok_when_file_matches(monkeypatch, capsys, tmp_path):
    md = render_charter(_ok_charter())
    file_sha = git_blob_sha(md.encode("utf-8"))
    store = InMemoryCharterStore()
    _put(store, _ok_charter(file_sha), CharterStatus.OK)
    monkeypatch.setattr("dsf.cli.charter.build_charter_store", lambda s: store)
    f = tmp_path / "charter.md"
    f.write_text(md)
    rc = main(["charter", "status", "--product", "alpha", "--file", str(f)])
    assert rc == 0 and "ok" in capsys.readouterr().out


def test_status_stale_on_sha_mismatch(monkeypatch, capsys, tmp_path):
    store = InMemoryCharterStore()
    _put(store, _ok_charter("oldsha"), CharterStatus.OK)
    monkeypatch.setattr("dsf.cli.charter.build_charter_store", lambda s: store)
    f = tmp_path / "charter.md"
    f.write_text(render_charter(_ok_charter()))
    rc = main(["charter", "status", "--product", "alpha", "--file", str(f)])
    assert rc == 0 and "stale" in capsys.readouterr().out


def test_status_missing_when_no_file(monkeypatch, capsys, tmp_path):
    monkeypatch.setattr("dsf.cli.charter.build_charter_store", lambda s: InMemoryCharterStore())
    rc = main(["charter", "status", "--product", "alpha", "--file", str(tmp_path / "nope.md")])
    assert rc == 0 and "missing" in capsys.readouterr().out


def test_status_ref_via_app(monkeypatch, capsys):
    store = InMemoryCharterStore()
    _put(store, _ok_charter("blobsha"), CharterStatus.OK)
    client = RecordingRepoClient({CHARTER_PATH: (render_charter(_ok_charter()), "blobsha")})
    monkeypatch.setattr("dsf.cli.charter.build_charter_store", lambda s: store)
    monkeypatch.setattr("dsf.cli.charter.build_repo_app_client", lambda s: client)
    monkeypatch.setattr("dsf.cli.charter._resolve_repo", lambda product: "org/alpha")
    rc = main(["charter", "status", "--product", "alpha", "--ref", "main"])
    assert rc == 0 and "ok" in capsys.readouterr().out


def test_sync_from_local_file(monkeypatch, capsys, tmp_path):
    store = InMemoryCharterStore()
    monkeypatch.setattr("dsf.cli.charter.build_charter_store", lambda s: store)
    f = tmp_path / "charter.md"
    f.write_text(render_charter(_ok_charter()))
    rc = main(["charter", "sync", "--product", "alpha", "--file", str(f)])
    assert rc == 0 and "OK" in capsys.readouterr().out
    assert asyncio.run(store.get_charter("alpha")).status == CharterStatus.OK


def test_sync_from_ref_uses_app(monkeypatch, capsys):
    store = InMemoryCharterStore()
    client = RecordingRepoClient({CHARTER_PATH: (render_charter(_ok_charter()), "blobsha")})
    monkeypatch.setattr("dsf.cli.charter.build_charter_store", lambda s: store)
    monkeypatch.setattr("dsf.cli.charter.build_repo_app_client", lambda s: client)
    monkeypatch.setattr("dsf.cli.charter._resolve_repo", lambda product: "org/alpha")
    rc = main(["charter", "sync", "--product", "alpha", "--ref", "main"])
    assert rc == 0 and "OK" in capsys.readouterr().out


def test_sync_invalid_file_returns_1(monkeypatch, capsys, tmp_path):
    monkeypatch.setattr("dsf.cli.charter.build_charter_store", lambda s: InMemoryCharterStore())
    f = tmp_path / "charter.md"
    f.write_text("garbage, no marker")
    rc = main(["charter", "sync", "--product", "alpha", "--file", str(f)])
    assert rc == 1 and "INVALID" in capsys.readouterr().out


def test_sync_ref_unknown_product(monkeypatch, capsys):
    monkeypatch.setattr("dsf.cli.charter.build_charter_store", lambda s: InMemoryCharterStore())
    monkeypatch.setattr("dsf.cli.charter._resolve_repo", lambda product: None)
    rc = main(["charter", "sync", "--product", "ghost", "--ref", "main"])
    err = capsys.readouterr().err
    assert rc == 1 and "cannot resolve repo for product" in err
    assert "DSF_OWNER_APPCONFIG_ENDPOINT" in err


async def test_run_interview_drives_to_draft():
    from dsf.charter.interview import CharterInterviewer, InterviewerTurn
    from dsf.cli.charter import _run_interview

    model = DeterministicModelClient()

    def handler(system: str, prompt: str):
        if prompt.count("user:") >= 1:
            return InterviewerTurn(message="done", done=True, draft=_ok_charter())
        return InterviewerTurn(message="What problem?", done=False)

    model.register("[charter-interview]", handler)
    iv = CharterInterviewer(model, "alpha")
    answers = iter(["slow dashboards"])
    draft = await _run_interview(iv, reader=lambda _: next(answers), writer=lambda *a: None)
    assert draft.vision == "V"


def test_init_opens_pr(monkeypatch, capsys):
    from dsf.charter.interview import InterviewerTurn

    model = DeterministicModelClient()
    model.register(
        "[charter-interview]",
        lambda s, p: InterviewerTurn(message="drafted", done=True, draft=_ok_charter()),
    )
    client = RecordingRepoClient({})
    monkeypatch.setattr("dsf.cli.charter._resolve_repo", lambda product: "org/alpha")
    monkeypatch.setattr("dsf.cli.charter.build_repo_app_client", lambda s: client)
    monkeypatch.setattr("dsf.cli.charter.build_model_client", lambda s: model)
    monkeypatch.setattr(
        "dsf.cli.charter.build_config_store", lambda s: InMemoryConfigStore.from_defaults()
    )
    monkeypatch.setattr("builtins.input", lambda *a: "answer")
    rc = main(["charter", "init", "--product", "alpha"])
    out = capsys.readouterr().out
    assert rc == 0 and "opened charter PR" in out
    assert len(client.prs) == 1 and client.prs[0]["path"] == CHARTER_PATH


def test_init_requires_app(monkeypatch, capsys):
    def _raise(_settings):
        raise ValueError("GitHub App is not fully configured")

    monkeypatch.setattr("dsf.cli.charter._resolve_repo", lambda product: "org/alpha")
    monkeypatch.setattr("dsf.cli.charter.build_repo_app_client", _raise)
    monkeypatch.setattr("dsf.cli.charter.build_model_client", lambda s: DeterministicModelClient())
    monkeypatch.setattr(
        "dsf.cli.charter.build_config_store", lambda s: InMemoryConfigStore.from_defaults()
    )
    rc = main(["charter", "init", "--product", "alpha"])
    assert rc == 1 and "App" in capsys.readouterr().err


def test_app_settings_derives_app_creds_from_owner_kv(monkeypatch):
    from dsf.cli.charter import _app_settings

    for var in (
        "GITHUB_APP_ID",
        "GITHUB_INSTALLATION_ID",
        "GITHUB_APP_PRIVATE_KEY_SECRET",
        "AZURE_KEYVAULT_URI",
        "GITHUB_REPOSITORY",
    ):
        monkeypatch.delenv(var, raising=False)
    monkeypatch.setenv("DSF_OWNER_KEYVAULT_URI", "https://owner-kv.vault.azure.net/")
    monkeypatch.setattr("dsf.cli.charter._resolve_repo", lambda product: "org/alpha")

    secrets = {"github-app-id": "111", "github-app-installation-id": "222"}
    settings = _app_settings("alpha", secret_reader=lambda kv, name: secrets[name])

    assert settings.github_app_id == "111"
    assert settings.github_installation_id == "222"
    assert settings.github_app_private_key_secret == "github-app-private-key"
    assert settings.keyvault_uri == "https://owner-kv.vault.azure.net/"
    assert settings.github_repository == "org/alpha"


def test_resolve_repo_reads_owner_index(monkeypatch):
    from dsf.cli import charter as charter_mod

    monkeypatch.setenv("DSF_OWNER_APPCONFIG_ENDPOINT", "https://owner")
    seen = {}

    def _fake_repo_for_product(endpoint, product, **_):
        seen["endpoint"] = endpoint
        seen["product"] = product
        return "org/alpha" if product == "alpha" else None

    monkeypatch.setattr("dsf.config.owner_index.repo_for_product", _fake_repo_for_product)

    assert charter_mod._resolve_repo("alpha") == "org/alpha"
    assert seen == {"endpoint": "https://owner", "product": "alpha"}
    assert charter_mod._resolve_repo("missing") is None


def test_resolve_repo_returns_none_without_owner_endpoint(monkeypatch):
    from dsf.cli import charter as charter_mod

    monkeypatch.delenv("DSF_OWNER_APPCONFIG_ENDPOINT", raising=False)
    monkeypatch.setattr(
        "dsf.config.owner_index.repo_for_product",
        lambda endpoint, product, **_: ("SHOULD-NOT-BE-USED" if endpoint else None),
    )
    assert charter_mod._resolve_repo("alpha") is None


def test_settings_fills_azure_endpoints_from_manifest(monkeypatch):
    from dsf.cli.charter import _settings

    for var in (
        "AZURE_OPENAI_ENDPOINT",
        "AZURE_OPENAI_DEPLOYMENT",
        "AZURE_APPCONFIG_ENDPOINT",
        "AZURE_COSMOS_ENDPOINT",
    ):
        monkeypatch.delenv(var, raising=False)
    monkeypatch.setattr(
        "dsf.cli.charter.read_manifest",
        lambda product: _fake_manifest(
            {
                "openaiEndpoint": "https://aif.example.com/",
                "openaiDeployment": "gpt-4o",
                "appConfigEndpoint": "https://appcs.example.io",
                "cosmosEndpoint": "https://cosmos.example.com:443/",
            }
        ),
    )
    s = _settings("pets")
    assert s.openai_endpoint == "https://aif.example.com/"
    assert s.openai_deployment == "gpt-4o"
    assert s.appconfig_endpoint == "https://appcs.example.io"
    assert s.cosmos_endpoint == "https://cosmos.example.com:443/"


def test_settings_explicit_env_overrides_manifest(monkeypatch):
    from dsf.cli.charter import _settings

    monkeypatch.setenv("AZURE_OPENAI_DEPLOYMENT", "explicit-deploy")
    monkeypatch.setattr(
        "dsf.cli.charter.read_manifest",
        lambda product: _fake_manifest({"openaiDeployment": "gpt-4o"}),
    )
    assert _settings("pets").openai_deployment == "explicit-deploy"


def test_settings_missing_manifest_is_blank(monkeypatch):
    from dsf.cli.charter import _settings

    for var in ("AZURE_OPENAI_ENDPOINT", "AZURE_OPENAI_DEPLOYMENT"):
        monkeypatch.delenv(var, raising=False)

    def _missing(product):
        raise FileNotFoundError(product)

    monkeypatch.setattr("dsf.cli.charter.read_manifest", _missing)
    s = _settings("ghost")
    assert s.openai_endpoint == "" and s.openai_deployment == ""


def test_app_settings_fills_endpoints_but_owner_kv_wins_for_keyvault(monkeypatch):
    from dsf.cli.charter import _app_settings

    for var in (
        "GITHUB_APP_ID",
        "GITHUB_INSTALLATION_ID",
        "GITHUB_APP_PRIVATE_KEY_SECRET",
        "AZURE_KEYVAULT_URI",
        "GITHUB_REPOSITORY",
        "AZURE_OPENAI_ENDPOINT",
    ):
        monkeypatch.delenv(var, raising=False)
    monkeypatch.setenv("DSF_OWNER_KEYVAULT_URI", "https://owner-kv.vault.azure.net/")
    monkeypatch.setattr("dsf.cli.charter._resolve_repo", lambda product: "org/alpha")
    monkeypatch.setattr(
        "dsf.cli.charter.read_manifest",
        lambda product: _fake_manifest(
            {
                "keyVaultUri": "https://product-kv.vault.azure.net/",
                "openaiEndpoint": "https://aif.example.com/",
            }
        ),
    )
    secrets = {"github-app-id": "111", "github-app-installation-id": "222"}
    settings = _app_settings("alpha", secret_reader=lambda kv, name: secrets[name])
    # owner KV backs the App private key, NOT the product vault from the manifest
    assert settings.keyvault_uri == "https://owner-kv.vault.azure.net/"
    # ...but the OpenAI endpoint is still gap-filled from the manifest
    assert settings.openai_endpoint == "https://aif.example.com/"
    assert settings.github_app_id == "111"


def test_app_settings_respects_explicit_env(monkeypatch):
    from dsf.cli.charter import _app_settings

    monkeypatch.setenv("DSF_OWNER_KEYVAULT_URI", "https://owner-kv.vault.azure.net/")
    monkeypatch.setenv("GITHUB_APP_ID", "explicit-app")
    monkeypatch.setenv("GITHUB_INSTALLATION_ID", "explicit-inst")
    monkeypatch.setenv("GITHUB_APP_PRIVATE_KEY_SECRET", "explicit-secret")
    monkeypatch.setenv("AZURE_KEYVAULT_URI", "https://product-kv.vault.azure.net/")

    def _boom(kv, name):
        raise AssertionError("must not read the owner KV when App env is explicit")

    settings = _app_settings("alpha", secret_reader=_boom)
    assert settings.github_app_id == "explicit-app"
    assert settings.keyvault_uri == "https://product-kv.vault.azure.net/"


def test_app_settings_no_owner_kv_stays_offline(monkeypatch):
    from dsf.cli.charter import _app_settings

    for var in (
        "GITHUB_APP_ID",
        "GITHUB_INSTALLATION_ID",
        "GITHUB_APP_PRIVATE_KEY_SECRET",
        "AZURE_KEYVAULT_URI",
        "DSF_OWNER_KEYVAULT_URI",
    ):
        monkeypatch.delenv(var, raising=False)

    def _boom(kv, name):
        raise AssertionError("must not read any KV without DSF_OWNER_KEYVAULT_URI")

    settings = _app_settings("alpha", secret_reader=_boom)
    assert settings.github_app_id == ""
    assert settings.product == "alpha"
