"""Tests for the shared teardown not-found classifier."""

from __future__ import annotations

import subprocess
from unittest.mock import MagicMock

import pytest

from dsf.instance.spec import InstanceSpec
from dsf.instance.teardown_common import (
    ALREADY_ABSENT_RESULT,
    AzureTeardown,
    is_not_found,
    is_not_found_text,
)


@pytest.mark.parametrize(
    "text",
    [
        "ResourceGroupNotFound: rg-dsf-sre-demo",
        "(ResourceNotFound) The Resource was not found.",
        "Could not resolve to a repository",
        "The vault was not found in this subscription",
        "Resource does not exist",
        "no resource group found",
    ],
)
def test_is_not_found_text_matches_absent_signals(text):
    assert is_not_found_text(text) is True


@pytest.mark.parametrize(
    "text",
    [
        "AuthorizationFailed: insufficient permissions",
        "the request timed out",
        # Bare "missing" is deliberately NOT a signal: too broad, would swallow
        # real teardown failures.
        "missing required argument --name",
        "",
    ],
)
def test_is_not_found_text_ignores_other_failures(text):
    assert is_not_found_text(text) is False


def test_is_not_found_reads_stderr_and_stdout():
    exc = subprocess.CalledProcessError(
        1, ["az", "group", "delete"], output="", stderr="ResourceGroupNotFound"
    )
    assert is_not_found(exc) is True

    real = subprocess.CalledProcessError(
        1, ["az", "group", "delete"], output="", stderr="AuthorizationFailed"
    )
    assert is_not_found(real) is False


def test_already_absent_result_wording():
    assert ALREADY_ABSENT_RESULT == "not-found (already absent)"


# ---------------------------------------------------------------------------
# AzureTeardown
# ---------------------------------------------------------------------------


def _ok(stdout: str = "") -> MagicMock:
    return MagicMock(returncode=0, stdout=stdout, stderr="")


def test_group_exists_true_and_false():
    az_true = AzureTeardown(lambda *a, **kw: _ok("true\n"))
    az_false = AzureTeardown(lambda *a, **kw: _ok("false\n"))
    assert az_true.group_exists("rg-x") is True
    assert az_false.group_exists("rg-x") is False


def test_delete_group_skips_when_absent():
    calls = []

    def run(cmd, **kwargs):
        calls.append(cmd)
        if cmd[:3] == ["az", "group", "exists"]:
            return _ok("false\n")
        return _ok()

    result = AzureTeardown(run).delete_group("rg-gone")
    assert result == ALREADY_ABSENT_RESULT
    assert not any(c[:3] == ["az", "group", "delete"] for c in calls)


def test_delete_group_deletes_when_present():
    def run(cmd, **kwargs):
        if cmd[:3] == ["az", "group", "exists"]:
            return _ok("true\n")
        return _ok()

    assert AzureTeardown(run).delete_group("rg-live") == "deleted"


def test_delete_group_tolerates_delete_race_404():
    def run(cmd, **kwargs):
        if cmd[:3] == ["az", "group", "exists"]:
            return _ok("true\n")
        raise subprocess.CalledProcessError(1, cmd, stderr="ResourceGroupNotFound")

    assert AzureTeardown(run).delete_group("rg-race") == ALREADY_ABSENT_RESULT


def test_delete_group_reraises_real_error():
    def run(cmd, **kwargs):
        if cmd[:3] == ["az", "group", "exists"]:
            return _ok("true\n")
        raise subprocess.CalledProcessError(1, cmd, stderr="AuthorizationFailed")

    with pytest.raises(subprocess.CalledProcessError):
        AzureTeardown(run).delete_group("rg-x")


def test_remove_sre_rbac_deletes_expected_scopes():
    spec = InstanceSpec(product="demo", owner="acme", monitored_resource_groups=["rg-shared"])
    calls = []

    def run(cmd, **kwargs):
        calls.append(cmd)
        if cmd[:4] == ["az", "identity", "show", "--resource-group"]:
            return _ok("principal-1\n")
        if cmd[:3] == ["az", "account", "show"]:
            return _ok("sub-xyz\n")
        return _ok()

    assert AzureTeardown(run).remove_sre_rbac(spec) == "removed"
    role_deletes = [c for c in calls if c[:4] == ["az", "role", "assignment", "delete"]]
    scopes = {cmd[cmd.index("--scope") + 1] for cmd in role_deletes}
    assert "/subscriptions/sub-xyz" in scopes
    assert "/subscriptions/sub-xyz/resourceGroups/rg-dsf-demo" in scopes
    assert "/subscriptions/sub-xyz/resourceGroups/rg-shared" in scopes
    # Subscription scope carries Monitoring Contributor.
    sub_delete = next(
        c for c in role_deletes if c[c.index("--scope") + 1] == "/subscriptions/sub-xyz"
    )
    assert "749f88d5-cbae-40b8-bcfc-e573ddc772fa" in sub_delete


def test_remove_sre_rbac_absent_identity_returns_already_absent():
    spec = InstanceSpec(product="demo", owner="acme")

    def run(cmd, **kwargs):
        if cmd[:4] == ["az", "identity", "show", "--resource-group"]:
            return MagicMock(returncode=3, stdout="", stderr="ResourceNotFound")
        return _ok()

    assert AzureTeardown(run).remove_sre_rbac(spec) == ALREADY_ABSENT_RESULT


def test_capture_tsv_returns_stdout_and_tolerates_not_found():
    az = AzureTeardown(lambda *a, **kw: _ok("  value \n"))
    assert az.capture_tsv(["az", "x"]) == "value"

    def run(cmd, **kwargs):
        return MagicMock(returncode=3, stdout="", stderr="ResourceNotFound")

    az_absent = AzureTeardown(run)
    assert az_absent.capture_tsv(["az", "x"], allow_not_found=True) == ""
    with pytest.raises(subprocess.CalledProcessError):
        az_absent.capture_tsv(["az", "x"])
