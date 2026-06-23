from __future__ import annotations

import asyncio

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
    assert rc == 1 and "not in registry" in capsys.readouterr().err


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
