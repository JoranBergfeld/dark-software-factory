from __future__ import annotations

import asyncio
from types import SimpleNamespace

from dsf.charter.constitution import CONSTITUTION_PATH, render_constitution
from dsf.charter.markdown import git_blob_sha, render_charter
from dsf.charter.sync import CHARTER_PATH
from dsf.cli import charter
from dsf.cli.factory import build_parser, main
from dsf.contracts.charter import Charter, StoredCharter
from dsf.contracts.enums import CharterStatus
from dsf.contracts.handoff import HANDOFF_LABEL
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


def test_status_closes_the_store(monkeypatch, capsys):
    closed = {"n": 0}

    class _ClosingStore(InMemoryCharterStore):
        async def aclose(self) -> None:
            closed["n"] += 1

    store = _ClosingStore()
    monkeypatch.setattr("dsf.cli.charter.build_charter_store", lambda s: store)
    monkeypatch.setattr("dsf.cli.charter._live_blob_sha", lambda a, p: (None, None))
    rc = main(["charter", "status", "--product", "alpha"])
    assert rc == 0
    assert closed["n"] == 1


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


def _seed_ok_implement(monkeypatch, *, create_issue_error=None):
    """Wire an OK, non-drifted charter + an App double for `charter implement`."""
    charter = _ok_charter("blobsha")
    store = InMemoryCharterStore()
    _put(store, charter, CharterStatus.OK)
    client = RecordingRepoClient(
        {CHARTER_PATH: (render_charter(charter), "blobsha")},
        create_issue_error=create_issue_error,
    )
    monkeypatch.setattr("dsf.cli.charter.build_charter_store", lambda s: store)
    monkeypatch.setattr("dsf.cli.charter.build_repo_app_client", lambda s: client)
    monkeypatch.setattr("dsf.cli.charter._resolve_repo", lambda product: "org/alpha")
    return client


def test_implement_subcommand_parses():
    parser = build_parser()
    args = parser.parse_args(["charter", "implement", "--product", "alpha"])
    assert args.command == "charter" and args.product == "alpha"


def test_implement_opens_constitution_pr_and_files_issue(monkeypatch, capsys):
    client = _seed_ok_implement(monkeypatch)
    rc = main(["charter", "implement", "--product", "alpha", "--no-wait"])
    out = capsys.readouterr().out
    assert rc == 0
    assert len(client.prs) == 1
    assert client.prs[0]["path"] == ".specify/memory/constitution.md"
    assert client.prs[0]["enable_auto_merge"] is True
    assert len(client.issues) == 1
    assert client.issues[0]["labels"] == [HANDOFF_LABEL]
    assert "opened constitution PR" in out and "filed bootstrap issue" in out
    assert "synced charter for alpha from main" in out


def test_implement_closes_the_store(monkeypatch, capsys):
    closed = {"n": 0}

    class _ClosingStore(InMemoryCharterStore):
        async def aclose(self) -> None:
            closed["n"] += 1

    store = _ClosingStore()
    _put(store, _ok_charter("blobsha"), CharterStatus.OK)
    client = RecordingRepoClient(
        {CHARTER_PATH: (render_charter(_ok_charter("blobsha")), "blobsha")}
    )
    monkeypatch.setattr("dsf.cli.charter.build_charter_store", lambda s: store)
    monkeypatch.setattr("dsf.cli.charter.build_repo_app_client", lambda s: client)
    monkeypatch.setattr("dsf.cli.charter._resolve_repo", lambda product: "org/alpha")
    rc = main(["charter", "implement", "--product", "alpha", "--no-wait"])
    assert rc == 0
    assert closed["n"] == 1


def test_implement_syncs_stale_charter_then_proceeds(monkeypatch, capsys):
    # A charter merged to main but not yet synced into Cosmos ("stale") must no
    # longer block: `implement` syncs from main first, then proceeds.
    store = InMemoryCharterStore()
    _put(store, _ok_charter("oldsha"), CharterStatus.OK)
    client = RecordingRepoClient({CHARTER_PATH: (render_charter(_ok_charter("newsha")), "newsha")})
    monkeypatch.setattr("dsf.cli.charter.build_charter_store", lambda s: store)
    monkeypatch.setattr("dsf.cli.charter.build_repo_app_client", lambda s: client)
    monkeypatch.setattr("dsf.cli.charter._resolve_repo", lambda product: "org/alpha")
    rc = main(["charter", "implement", "--product", "alpha", "--no-wait"])
    out = capsys.readouterr().out
    assert rc == 0
    assert "synced charter for alpha from main" in out
    assert len(client.prs) == 1 and len(client.issues) == 1
    # the stale charter was refreshed into the store from main
    assert asyncio.run(store.get_charter("alpha")).charter.source_sha == "newsha"


def test_implement_refuses_when_charter_invalid_on_main(monkeypatch, capsys):
    store = InMemoryCharterStore()
    client = RecordingRepoClient({CHARTER_PATH: ("garbage, no marker", "badsha")})
    monkeypatch.setattr("dsf.cli.charter.build_charter_store", lambda s: store)
    monkeypatch.setattr("dsf.cli.charter.build_repo_app_client", lambda s: client)
    monkeypatch.setattr("dsf.cli.charter._resolve_repo", lambda product: "org/alpha")
    rc = main(["charter", "implement", "--product", "alpha"])
    assert rc == 1 and "invalid" in capsys.readouterr().err
    assert not client.prs and not client.issues


def test_implement_refuses_when_charter_missing(monkeypatch, capsys):
    store = InMemoryCharterStore()
    client = RecordingRepoClient({})  # read_file -> None -> sync records MISSING
    monkeypatch.setattr("dsf.cli.charter.build_charter_store", lambda s: store)
    monkeypatch.setattr("dsf.cli.charter.build_repo_app_client", lambda s: client)
    monkeypatch.setattr("dsf.cli.charter._resolve_repo", lambda product: "org/alpha")
    rc = main(["charter", "implement", "--product", "alpha"])
    assert rc == 1 and "missing" in capsys.readouterr().err
    assert not client.prs and not client.issues


def test_implement_unknown_product(monkeypatch, capsys):
    monkeypatch.setattr("dsf.cli.charter._resolve_repo", lambda product: None)
    rc = main(["charter", "implement", "--product", "ghost"])
    assert rc == 1 and "not in registry" in capsys.readouterr().err


def test_implement_falls_back_to_gh_when_app_assignment_forbidden(monkeypatch, capsys):
    from dsf.ports import CodingAgentAssignmentError

    # App installation tokens can't assign agents; `implement` retries the assign
    # step with the operator's gh user token and succeeds.
    boom = CodingAgentAssignmentError(
        "installation token", issue_url="local://issue/1", issue_node_id="ISSUE_NODE_1"
    )
    client = _seed_ok_implement(monkeypatch, create_issue_error=boom)
    calls: list[tuple[str, str]] = []
    monkeypatch.setattr(
        "dsf.cli.charter._assign_copilot_via_gh",
        lambda repo, node_id: calls.append((repo, node_id)) or True,
    )
    rc = main(["charter", "implement", "--product", "alpha", "--no-wait"])
    out = capsys.readouterr().out
    assert rc == 0
    assert calls == [("org/alpha", "ISSUE_NODE_1")]
    assert "assigned Copilot via gh" in out
    assert len(client.prs) == 1  # constitution PR still opened


def test_implement_warns_when_gh_assignment_also_fails(monkeypatch, capsys):
    from dsf.ports import CodingAgentAssignmentError

    boom = CodingAgentAssignmentError(
        "installation token", issue_url="local://issue/1", issue_node_id="ISSUE_NODE_1"
    )
    client = _seed_ok_implement(monkeypatch, create_issue_error=boom)
    monkeypatch.setattr("dsf.cli.charter._assign_copilot_via_gh", lambda repo, node_id: False)
    rc = main(["charter", "implement", "--product", "alpha"])
    captured = capsys.readouterr()
    assert rc == 0  # filing succeeded; a failed assign is a warning, not a hard fail
    assert "filed bootstrap issue: local://issue/1" in captured.out
    assert "could not assign" in captured.err
    assert len(client.prs) == 1  # constitution PR still opened


def test_implement_watches_build_by_default(monkeypatch, capsys):
    _seed_ok_implement(monkeypatch)
    seen = {}
    monkeypatch.setattr(
        "dsf.cli.charter._watch_and_request_review",
        lambda repo, issue, **kw: seen.update(repo=repo, issue=issue) or 0,
    )
    rc = main(["charter", "implement", "--product", "alpha"])
    assert rc == 0
    # RecordingRepoClient.create_issue returns local://issue/1 -> issue number 1
    assert seen == {"repo": "org/alpha", "issue": 1}


def test_implement_no_wait_skips_watch(monkeypatch, capsys):
    _seed_ok_implement(monkeypatch)
    called = {"n": 0}
    monkeypatch.setattr(
        "dsf.cli.charter._watch_and_request_review",
        lambda *a, **k: called.__setitem__("n", called["n"] + 1) or 0,
    )
    rc = main(["charter", "implement", "--product", "alpha", "--no-wait"])
    assert rc == 0 and called["n"] == 0
    assert "dsf charter watch" in capsys.readouterr().out


# --- gh-based Copilot assignment (laptop `implement`) -------------------------


def test_gh_graphql_builds_command_and_parses(monkeypatch):
    from dsf.cli import charter as charter_mod

    seen: dict[str, list[str]] = {}

    def fake_run(argv, **kwargs):
        seen["argv"] = argv
        return SimpleNamespace(stdout='{"data":{"ok":true}}', returncode=0)

    monkeypatch.setattr("subprocess.run", fake_run)
    data = charter_mod._gh_graphql("QUERY", owner="o", name="n")
    assert data == {"ok": True}
    argv = seen["argv"]
    assert argv[:3] == ["gh", "api", "graphql"]
    assert "query=QUERY" in argv and "owner=o" in argv and "name=n" in argv


def test_gh_graphql_passes_int_vars(monkeypatch):
    seen = {}

    def fake_run(argv, check, capture_output, text):
        seen["argv"] = argv
        return SimpleNamespace(stdout='{"data":{"ok":true}}')

    monkeypatch.setattr("subprocess.run", fake_run)
    data = charter._gh_graphql("query($num:Int!){x}", int_vars={"num": 7}, owner="o")
    assert data == {"ok": True}
    assert "-F" in seen["argv"] and "num=7" in seen["argv"]
    assert "-f" in seen["argv"] and "owner=o" in seen["argv"]


def test_gh_graphql_raises_on_graphql_error(monkeypatch):
    from dsf.cli import charter as charter_mod

    monkeypatch.setattr(
        "subprocess.run",
        lambda argv, **kwargs: SimpleNamespace(stdout='{"errors":[{"message":"boom"}]}'),
    )
    try:
        charter_mod._gh_graphql("QUERY")
    except RuntimeError as exc:
        assert "GraphQL error" in str(exc)
    else:
        raise AssertionError("expected RuntimeError on GraphQL error")


def test_find_agent_pr_selects_copilot_pr(monkeypatch):
    def fake_graphql(query, *, int_vars=None, **variables):
        return {
            "repository": {
                "issue": {
                    "timelineItems": {
                        "nodes": [
                            {
                                "__typename": "CrossReferencedEvent",
                                "source": {
                                    "__typename": "PullRequest",
                                    "number": 8,
                                    "url": "https://x/pull/8",
                                    "isDraft": True,
                                    "state": "OPEN",
                                    "author": {"login": "copilot-swe-agent"},
                                },
                            }
                        ]
                    }
                }
            }
        }

    monkeypatch.setattr("dsf.cli.charter._gh_graphql", fake_graphql)
    pr = charter._find_agent_pr("org/alpha", 7)
    assert pr == {"number": 8, "url": "https://x/pull/8", "is_draft": True, "state": "OPEN"}


def test_find_agent_pr_none_when_no_copilot_pr(monkeypatch):
    monkeypatch.setattr(
        "dsf.cli.charter._gh_graphql",
        lambda *a, **k: {"repository": {"issue": {"timelineItems": {"nodes": []}}}},
    )
    assert charter._find_agent_pr("org/alpha", 7) is None


def _timeline(nodes):
    return {"repository": {"issue": {"timelineItems": {"nodes": nodes}}}}


def _xref_pr(number, *, url=None, is_draft=False, state="OPEN", login="copilot-swe-agent"):
    return {
        "__typename": "CrossReferencedEvent",
        "source": {
            "__typename": "PullRequest",
            "number": number,
            "url": url or f"https://x/pull/{number}",
            "isDraft": is_draft,
            "state": state,
            "author": {"login": login},
        },
    }


def test_find_agent_pr_ignores_non_copilot_prs(monkeypatch):
    monkeypatch.setattr(
        "dsf.cli.charter._gh_graphql",
        lambda *a, **k: _timeline([_xref_pr(3, login="some-human")]),
    )
    assert charter._find_agent_pr("org/alpha", 7) is None


def test_find_agent_pr_prefers_open_over_higher_numbered_closed(monkeypatch):
    monkeypatch.setattr(
        "dsf.cli.charter._gh_graphql",
        lambda *a, **k: _timeline([_xref_pr(20, state="CLOSED"), _xref_pr(2, state="OPEN")]),
    )
    pr = charter._find_agent_pr("org/alpha", 7)
    assert pr["number"] == 2 and pr["state"] == "OPEN"


def test_find_agent_pr_reads_connected_event_subject(monkeypatch):
    node = {
        "__typename": "ConnectedEvent",
        "subject": {
            "__typename": "PullRequest",
            "number": 9,
            "url": "https://x/pull/9",
            "isDraft": True,
            "state": "OPEN",
            "author": {"login": "app/copilot-swe-agent"},
        },
    }
    monkeypatch.setattr("dsf.cli.charter._gh_graphql", lambda *a, **k: _timeline([node]))
    pr = charter._find_agent_pr("org/alpha", 7)
    assert pr == {"number": 9, "url": "https://x/pull/9", "is_draft": True, "state": "OPEN"}


def test_request_copilot_review_runs_gh_pr_edit(monkeypatch):
    calls = []

    def fake_run(argv, check, capture_output, text):
        assert check is True
        return calls.append(argv) or SimpleNamespace(stdout="")

    monkeypatch.setattr(
        "subprocess.run",
        fake_run,
    )
    charter._request_copilot_review("org/alpha", "https://x/pull/8")
    assert calls == [
        [
            "gh",
            "pr",
            "edit",
            "https://x/pull/8",
            "--repo",
            "org/alpha",
            "--add-reviewer",
            "@copilot",
        ]
    ]


def test_pr_has_copilot_reviewer_true(monkeypatch):
    monkeypatch.setattr(
        "dsf.cli.charter._gh_graphql",
        lambda *a, **k: {
            "repository": {
                "pullRequest": {
                    "reviewRequests": {
                        "nodes": [
                            {
                                "requestedReviewer": {
                                    "__typename": "Bot",
                                    "login": "copilot-pull-request-reviewer",
                                }
                            }
                        ]
                    }
                }
            }
        },
    )
    assert charter._pr_has_copilot_reviewer("org/alpha", 8) is True


def test_pr_has_copilot_reviewer_false(monkeypatch):
    monkeypatch.setattr(
        "dsf.cli.charter._gh_graphql",
        lambda *a, **k: {
            "repository": {
                "pullRequest": {
                    "reviewRequests": {
                        "nodes": [
                            {"requestedReviewer": {"__typename": "User", "login": "octocat"}},
                            {"requestedReviewer": None},
                        ]
                    }
                }
            }
        },
    )
    assert charter._pr_has_copilot_reviewer("org/alpha", 8) is False


def test_assign_copilot_via_gh_success(monkeypatch):
    from dsf.cli import charter as charter_mod

    seen: list[tuple[str, dict]] = []

    def fake_gql(query, **variables):
        seen.append((query, variables))
        if "suggestedActors" in query:
            return {
                "repository": {
                    "suggestedActors": {
                        "nodes": [
                            {"login": "copilot-swe-agent", "id": "BOT_1"},
                            {"login": "someuser", "id": "U_1"},
                        ]
                    }
                }
            }
        return {"replaceActorsForAssignable": {"assignable": {"__typename": "Issue"}}}

    monkeypatch.setattr("dsf.cli.charter._gh_graphql", fake_gql)
    assert charter_mod._assign_copilot_via_gh("org/alpha", "ISSUE_NODE_1") is True
    mutation = next(c for c in seen if "replaceActorsForAssignable" in c[0])
    assert mutation[1]["assignableId"] == "ISSUE_NODE_1"
    assert mutation[1]["actorId"] == "BOT_1"


def test_assign_copilot_via_gh_false_when_copilot_absent(monkeypatch):
    from dsf.cli import charter as charter_mod

    monkeypatch.setattr(
        "dsf.cli.charter._gh_graphql",
        lambda query, **variables: {
            "repository": {"suggestedActors": {"nodes": [{"login": "someuser", "id": "U_1"}]}}
        },
    )
    assert charter_mod._assign_copilot_via_gh("org/alpha", "N") is False


def test_assign_copilot_via_gh_false_on_gh_error(monkeypatch):
    import subprocess

    from dsf.cli import charter as charter_mod

    def boom(query, **variables):
        raise subprocess.CalledProcessError(1, ["gh"])

    monkeypatch.setattr("dsf.cli.charter._gh_graphql", boom)
    assert charter_mod._assign_copilot_via_gh("org/alpha", "N") is False


def _watch_env(monkeypatch, pr_states, *, reviewer=False, finished=False):
    """Drive _find_agent_pr through a scripted list of PR snapshots (or None)."""
    seq = list(pr_states)

    def fake_find(repo, issue):
        return seq.pop(0) if seq else pr_states[-1]

    requested = {"n": 0, "ready": 0}
    monkeypatch.setattr("dsf.cli.charter._find_agent_pr", fake_find)
    monkeypatch.setattr("dsf.cli.charter._agent_work_finished", lambda repo, num: finished)
    monkeypatch.setattr(
        "dsf.cli.charter._mark_pr_ready",
        lambda repo, num: requested.__setitem__("ready", requested["ready"] + 1),
    )
    monkeypatch.setattr(
        "dsf.cli.charter._pr_has_copilot_reviewer", lambda repo, num: reviewer
    )
    monkeypatch.setattr(
        "dsf.cli.charter._request_copilot_review",
        lambda repo, url: requested.__setitem__("n", requested["n"] + 1),
    )
    return requested


def test_watch_marks_ready_and_requests_review_when_agent_finished(monkeypatch, capsys):
    draft = {"number": 8, "url": "https://x/pull/8", "is_draft": True, "state": "OPEN"}
    requested = _watch_env(monkeypatch, [draft], finished=True)
    rc = charter._watch_and_request_review(
        "org/alpha", 7, timeout=None, poll_interval=0.0, sleep=lambda s: None
    )
    out = capsys.readouterr().out
    assert rc == 0
    assert requested["ready"] == 1
    assert requested["n"] == 1
    assert "marked ready for review" in out
    assert "requested Copilot review" in out


def test_watch_does_not_mark_ready_while_still_building(monkeypatch, capsys):
    draft = {"number": 8, "url": "https://x/pull/8", "is_draft": True, "state": "OPEN"}
    requested = _watch_env(monkeypatch, [draft], finished=False)
    clock = iter([0.0, 5.0, 999.0])
    rc = charter._watch_and_request_review(
        "org/alpha",
        7,
        timeout=10.0,
        poll_interval=0.0,
        sleep=lambda s: None,
        clock=lambda: next(clock),
    )
    out = capsys.readouterr().out
    assert rc == 2
    assert requested["ready"] == 0
    assert requested["n"] == 0
    assert "building (draft)" in out


def test_watch_marks_ready_when_draft_finishes_after_polling(monkeypatch):
    draft = {"number": 8, "url": "https://x/pull/8", "is_draft": True, "state": "OPEN"}
    requested = _watch_env(monkeypatch, [None, draft, draft], finished=True)
    rc = charter._watch_and_request_review(
        "org/alpha", 7, timeout=None, poll_interval=0.0, sleep=lambda s: None
    )
    assert rc == 0
    assert requested["ready"] == 1
    assert requested["n"] == 1


def test_agent_work_finished_reads_last_copilot_event(monkeypatch):
    import json

    seen = []
    payloads = iter(
        [
            [{"event": "copilot_work_started"}, {"event": "copilot_work_finished"}],
            [{"event": "copilot_work_finished"}, {"event": "copilot_work_started"}],
            [{"event": "commented"}, "not-a-dict"],
        ]
    )

    def fake_run(argv, check, capture_output, text):
        seen.append(argv)
        return SimpleNamespace(stdout=json.dumps(next(payloads)))

    monkeypatch.setattr("subprocess.run", fake_run)
    assert charter._agent_work_finished("org/alpha", 8) is True
    assert charter._agent_work_finished("org/alpha", 8) is False
    assert charter._agent_work_finished("org/alpha", 8) is False
    assert seen[0] == [
        "gh",
        "api",
        "--paginate",
        "repos/org/alpha/issues/8/timeline",
    ]


def test_watch_requests_review_when_pr_becomes_ready(monkeypatch, capsys):
    draft = {"number": 8, "url": "https://x/pull/8", "is_draft": True, "state": "OPEN"}
    ready = {"number": 8, "url": "https://x/pull/8", "is_draft": False, "state": "OPEN"}
    requested = _watch_env(monkeypatch, [None, draft, ready])
    rc = charter._watch_and_request_review(
        "org/alpha", 7, timeout=None, poll_interval=0.0, sleep=lambda s: None
    )
    out = capsys.readouterr().out
    assert rc == 0 and requested["n"] == 1
    assert "requested Copilot review" in out


def test_watch_skips_when_review_already_requested(monkeypatch, capsys):
    ready = {"number": 8, "url": "https://x/pull/8", "is_draft": False, "state": "OPEN"}
    requested = _watch_env(monkeypatch, [ready], reviewer=True)
    rc = charter._watch_and_request_review(
        "org/alpha", 7, timeout=None, poll_interval=0.0, sleep=lambda s: None
    )
    assert rc == 0 and requested["n"] == 0
    assert "already requested" in capsys.readouterr().out


def test_watch_stops_when_pr_closed(monkeypatch, capsys):
    closed = {"number": 8, "url": "https://x/pull/8", "is_draft": True, "state": "CLOSED"}
    requested = _watch_env(monkeypatch, [closed])
    rc = charter._watch_and_request_review(
        "org/alpha", 7, timeout=None, poll_interval=0.0, sleep=lambda s: None
    )
    assert rc == 0 and requested["n"] == 0
    assert "closed" in capsys.readouterr().out.lower()


def test_watch_times_out(monkeypatch, capsys):
    draft = {"number": 8, "url": "https://x/pull/8", "is_draft": True, "state": "OPEN"}
    requested = _watch_env(monkeypatch, [draft])
    clock = iter([0.0, 5.0, 999.0])
    rc = charter._watch_and_request_review(
        "org/alpha", 7, timeout=10.0, poll_interval=0.0,
        sleep=lambda s: None, clock=lambda: next(clock),
    )
    assert rc == 2 and requested["n"] == 0
    assert "re-run" in capsys.readouterr().out


def test_watch_survives_transient_errors(monkeypatch, capsys):
    """A transient GraphQL/subprocess error is logged and retried, not fatal."""
    import subprocess

    ready = {"number": 8, "url": "https://x/pull/8", "is_draft": False, "state": "OPEN"}
    calls = {"n": 0}

    def flaky_find(repo, issue):
        calls["n"] += 1
        if calls["n"] == 1:
            raise RuntimeError("GraphQL error: rate limited")
        if calls["n"] == 2:
            raise subprocess.CalledProcessError(1, ["gh"], stderr="502 Bad Gateway")
        return ready

    requested = {"n": 0}
    monkeypatch.setattr("dsf.cli.charter._find_agent_pr", flaky_find)
    monkeypatch.setattr("dsf.cli.charter._pr_has_copilot_reviewer", lambda r, n: False)
    monkeypatch.setattr(
        "dsf.cli.charter._request_copilot_review",
        lambda r, u: requested.__setitem__("n", requested["n"] + 1),
    )
    rc = charter._watch_and_request_review(
        "org/alpha", 7, timeout=None, poll_interval=0.0, sleep=lambda s: None
    )
    out = capsys.readouterr().out
    assert rc == 0 and requested["n"] == 1
    assert calls["n"] == 3
    assert "transient" in out.lower()


def test_watch_survives_malformed_response(monkeypatch, capsys):
    """A null/malformed GraphQL payload (None[...] -> TypeError, or a missing
    key -> KeyError) is treated as a transient blip, not a fatal crash."""
    ready = {"number": 8, "url": "https://x/pull/8", "is_draft": False, "state": "OPEN"}
    calls = {"n": 0}

    def flaky_find(repo, issue):
        calls["n"] += 1
        if calls["n"] == 1:
            raise TypeError("'NoneType' object is not subscriptable")
        if calls["n"] == 2:
            raise KeyError("nodes")
        return ready

    requested = {"n": 0}
    monkeypatch.setattr("dsf.cli.charter._find_agent_pr", flaky_find)
    monkeypatch.setattr("dsf.cli.charter._pr_has_copilot_reviewer", lambda r, n: False)
    monkeypatch.setattr(
        "dsf.cli.charter._request_copilot_review",
        lambda r, u: requested.__setitem__("n", requested["n"] + 1),
    )
    rc = charter._watch_and_request_review(
        "org/alpha", 7, timeout=None, poll_interval=0.0, sleep=lambda s: None
    )
    out = capsys.readouterr().out
    assert rc == 0 and requested["n"] == 1
    assert calls["n"] == 3
    assert "transient" in out.lower()


def test_watch_subcommand_parses():
    parser = build_parser()
    args = parser.parse_args(["charter", "watch", "--product", "alpha", "--issue", "7"])
    assert args.command == "charter" and args.product == "alpha" and args.issue == 7


def test_watch_command_uses_explicit_issue(monkeypatch):
    monkeypatch.setattr("dsf.cli.charter._resolve_repo", lambda product: "org/alpha")
    seen = {}
    monkeypatch.setattr(
        "dsf.cli.charter._watch_and_request_review",
        lambda repo, issue, **kw: seen.update(repo=repo, issue=issue) or 0,
    )
    rc = main(["charter", "watch", "--product", "alpha", "--issue", "7"])
    assert rc == 0 and seen == {"repo": "org/alpha", "issue": 7}


def test_watch_command_finds_newest_handoff_issue(monkeypatch):
    monkeypatch.setattr("dsf.cli.charter._resolve_repo", lambda product: "org/alpha")
    monkeypatch.setattr("dsf.cli.charter._newest_handoff_issue", lambda repo: 42)
    seen = {}
    monkeypatch.setattr(
        "dsf.cli.charter._watch_and_request_review",
        lambda repo, issue, **kw: seen.update(issue=issue) or 0,
    )
    rc = main(["charter", "watch", "--product", "alpha"])
    assert rc == 0 and seen == {"issue": 42}


def test_watch_command_errors_when_no_issue(monkeypatch, capsys):
    monkeypatch.setattr("dsf.cli.charter._resolve_repo", lambda product: "org/alpha")
    monkeypatch.setattr("dsf.cli.charter._newest_handoff_issue", lambda repo: None)
    rc = main(["charter", "watch", "--product", "alpha"])
    assert rc == 1 and "no open" in capsys.readouterr().err.lower()


def test_newest_handoff_issue_picks_max(monkeypatch):
    captured = {}

    def fake_run(argv, check, capture_output, text):
        captured["argv"] = argv
        return SimpleNamespace(
            returncode=0,
            stdout='[{"number": 5}, {"number": 12}, {"number": 9}]',
            stderr="",
        )

    monkeypatch.setattr("subprocess.run", fake_run)
    assert charter._newest_handoff_issue("org/alpha") == 12
    argv = captured["argv"]
    assert argv[:5] == ["gh", "issue", "list", "--repo", "org/alpha"]
    assert "--label" in argv and HANDOFF_LABEL in argv
    assert "--state" in argv and "open" in argv
    assert argv[argv.index("--json") + 1] == "number"


def test_newest_handoff_issue_empty_returns_none(monkeypatch):
    monkeypatch.setattr(
        "subprocess.run",
        lambda argv, check, capture_output, text: SimpleNamespace(
            returncode=0, stdout="[]", stderr=""
        ),
    )
    assert charter._newest_handoff_issue("org/alpha") is None


def test_newest_handoff_issue_swallows_subprocess_error(monkeypatch):
    import subprocess

    def boom(argv, check, capture_output, text):
        raise subprocess.CalledProcessError(1, argv, stderr="gh failed")

    monkeypatch.setattr("subprocess.run", boom)
    assert charter._newest_handoff_issue("org/alpha") is None


def test_newest_handoff_issue_swallows_bad_json(monkeypatch):
    monkeypatch.setattr(
        "subprocess.run",
        lambda argv, check, capture_output, text: SimpleNamespace(
            returncode=0, stdout="not json", stderr=""
        ),
    )
    assert charter._newest_handoff_issue("org/alpha") is None


def test_recording_repo_client_scripts_read_file_sequence():
    client = RecordingRepoClient(
        read_file_sequence={CONSTITUTION_PATH: [None, ("first", "s1"), ("last", "s2")]}
    )
    # pops through the sequence in order, then the final entry sticks
    assert asyncio.run(client.read_file("r", CONSTITUTION_PATH)) is None
    assert asyncio.run(client.read_file("r", CONSTITUTION_PATH)).text == "first"
    assert asyncio.run(client.read_file("r", CONSTITUTION_PATH)).text == "last"
    assert asyncio.run(client.read_file("r", CONSTITUTION_PATH)).text == "last"
    # a path without a scripted sequence still falls back to static files
    assert asyncio.run(client.read_file("r", "absent")) is None


def test_ensure_constitution_pr_skips_when_already_current():
    ch = _ok_charter("blobsha")
    client = RecordingRepoClient({CONSTITUTION_PATH: (render_constitution(ch), "csha")})
    pr_url, already = asyncio.run(
        charter._ensure_constitution_pr(client, "org/alpha", "alpha", ch, render_constitution(ch))
    )
    assert already is True and pr_url is None and client.prs == []


def test_ensure_constitution_pr_reuses_open_same_revision_pr():
    from datetime import UTC, datetime

    from dsf_testing.github import SeedPr

    ch = _ok_charter("blobsha")  # sha8 == "blobsha"
    client = RecordingRepoClient(
        prs=[
            SeedPr(
                html_url="https://github.com/org/alpha/pull/7",
                state="open",
                created_at=datetime(2026, 7, 14, tzinfo=UTC),
                head_ref="charter/constitution-blobsha-deadbeef",
            )
        ]
    )
    pr_url, already = asyncio.run(
        charter._ensure_constitution_pr(client, "org/alpha", "alpha", ch, render_constitution(ch))
    )
    assert already is False
    assert pr_url == "https://github.com/org/alpha/pull/7"
    assert client.prs == []  # reused; no new PR opened


def test_ensure_constitution_pr_ignores_stale_revision_pr():
    from datetime import UTC, datetime

    from dsf_testing.github import SeedPr

    ch = _ok_charter("blobsha")  # current sha8; seeded PR below uses a different one
    client = RecordingRepoClient(
        prs=[
            SeedPr(
                html_url="https://github.com/org/alpha/pull/3",
                state="open",
                created_at=datetime(2026, 7, 13, tzinfo=UTC),
                head_ref="charter/constitution-oldsha00-deadbeef",  # different sha8
            )
        ]
    )
    pr_url, already = asyncio.run(
        charter._ensure_constitution_pr(client, "org/alpha", "alpha", ch, render_constitution(ch))
    )
    assert already is False
    assert len(client.prs) == 1
    assert client.prs[0]["branch"].startswith("charter/constitution-blobsha-")
    assert client.prs[0]["enable_auto_merge"] is True


def test_ensure_constitution_pr_ignores_closed_same_revision_pr():
    from datetime import UTC, datetime

    from dsf_testing.github import SeedPr

    ch = _ok_charter("blobsha")  # same sha8 as the seeded PR, but that PR is closed
    client = RecordingRepoClient(
        prs=[
            SeedPr(
                html_url="https://github.com/org/alpha/pull/5",
                state="closed",
                created_at=datetime(2026, 7, 14, tzinfo=UTC),
                head_ref="charter/constitution-blobsha-deadbeef",
            )
        ]
    )
    pr_url, already = asyncio.run(
        charter._ensure_constitution_pr(client, "org/alpha", "alpha", ch, render_constitution(ch))
    )
    assert already is False
    assert len(client.prs) == 1  # closed PR not reused -> a fresh PR is opened
    assert client.prs[0]["branch"].startswith("charter/constitution-blobsha-")
    assert pr_url.endswith("/pull/1")


def test_ensure_constitution_pr_opens_when_none_exists():
    ch = _ok_charter("blobsha")
    client = RecordingRepoClient({})
    pr_url, already = asyncio.run(
        charter._ensure_constitution_pr(client, "org/alpha", "alpha", ch, render_constitution(ch))
    )
    assert already is False
    assert len(client.prs) == 1
    assert client.prs[0]["path"] == CONSTITUTION_PATH
    assert pr_url.endswith("/pull/1")


def _fake_async_sleep(record):
    async def _sleep(seconds):
        record.append(seconds)

    return _sleep


def test_wait_returns_true_when_main_becomes_current():
    ch = _ok_charter("blobsha")
    current = render_constitution(ch)
    client = RecordingRepoClient(
        read_file_sequence={CONSTITUTION_PATH: [None, None, (current, "s")]}
    )
    slept: list[float] = []
    merged = asyncio.run(
        charter._wait_for_constitution_on_main(
            client,
            "org/alpha",
            ch,
            timeout=None,
            poll_interval=5,
            sleep=_fake_async_sleep(slept),
            clock=lambda: 0.0,
        )
    )
    assert merged is True
    assert len(slept) == 2  # two "not yet" polls before it went current


def test_wait_returns_false_on_timeout():
    ch = _ok_charter("blobsha")
    client = RecordingRepoClient(read_file_sequence={CONSTITUTION_PATH: [None]})
    ticks = iter([0.0, 0.0, 10.0])  # start, guard#1 (< timeout), guard#2 (>= timeout)
    merged = asyncio.run(
        charter._wait_for_constitution_on_main(
            client,
            "org/alpha",
            ch,
            timeout=5,
            poll_interval=1,
            sleep=_fake_async_sleep([]),
            clock=lambda: next(ticks),
        )
    )
    assert merged is False


def test_wait_retries_transient_errors_until_current():
    from types import SimpleNamespace

    ch = _ok_charter("blobsha")
    current = render_constitution(ch)

    class _Flaky:
        def __init__(self) -> None:
            self.calls = 0

        async def read_file(self, repo, path, ref="main"):
            self.calls += 1
            if self.calls == 1:
                raise RuntimeError("transient blip")
            return SimpleNamespace(text=current, sha="s", ref=ref)

    app = _Flaky()
    slept: list[float] = []
    merged = asyncio.run(
        charter._wait_for_constitution_on_main(
            app,
            "org/alpha",
            ch,
            timeout=None,
            poll_interval=1,
            sleep=_fake_async_sleep(slept),
            clock=lambda: 0.0,
        )
    )
    assert merged is True
    assert app.calls == 2 and len(slept) == 1
