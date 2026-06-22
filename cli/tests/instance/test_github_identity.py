"""Tests for resolving the GitHub owner for `dsf new` via the gh CLI fallback."""

from __future__ import annotations

import subprocess
import types

import pytest

from dsf.instance.github_identity import OwnerResolutionError, resolve_owner


def _ok_run(login: str):
    """A subprocess.run-compatible stub that returns ``login`` on stdout."""

    def _run(cmd, **kwargs):
        return types.SimpleNamespace(stdout=login, stderr="", returncode=0)

    return _run


def _raising_run(exc: BaseException):
    """A subprocess.run-compatible stub that raises ``exc``."""

    def _run(cmd, **kwargs):
        raise exc

    return _run


def test_supplied_owner_is_returned_without_invoking_gh():
    def _run(*_a, **_k):
        raise AssertionError("gh must not run when --owner is supplied")

    assert resolve_owner("acme", run=_run) == "acme"


def test_infers_login_and_warns_when_noninteractive(capsys):
    owner = resolve_owner("", run=_ok_run("octocat\n"), interactive=False)
    assert owner == "octocat"
    err = capsys.readouterr().err
    assert "octocat" in err
    assert "warning" in err.lower()


def test_prompts_and_confirms_when_interactive():
    owner = resolve_owner(
        "", run=_ok_run("octocat\n"), interactive=True, prompt=lambda _msg: "y"
    )
    assert owner == "octocat"


def test_aborts_when_interactive_user_declines():
    with pytest.raises(OwnerResolutionError):
        resolve_owner(
            "", run=_ok_run("octocat\n"), interactive=True, prompt=lambda _msg: "n"
        )


def test_errors_when_gh_not_installed():
    with pytest.raises(OwnerResolutionError) as exc:
        resolve_owner("", run=_raising_run(FileNotFoundError()), interactive=False)
    assert "--owner" in str(exc.value)


def test_errors_when_gh_unauthenticated():
    err = subprocess.CalledProcessError(1, ["gh"], stderr="not logged in")
    with pytest.raises(OwnerResolutionError):
        resolve_owner("", run=_raising_run(err), interactive=False)


def test_errors_when_login_is_empty():
    with pytest.raises(OwnerResolutionError):
        resolve_owner("", run=_ok_run("   \n"), interactive=False)
