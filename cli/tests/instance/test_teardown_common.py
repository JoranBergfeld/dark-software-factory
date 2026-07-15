"""Tests for the shared teardown not-found classifier."""

from __future__ import annotations

import json
import subprocess
from unittest.mock import MagicMock

import pytest

from dsf.instance.spec import InstanceSpec
from dsf.instance.teardown_common import (
    ALREADY_ABSENT_RESULT,
    AzureTeardown,
    ForeignResourceGroupError,
    group_tags,
    guarded_group_delete,
    is_not_found,
    is_not_found_text,
    is_purge_protected,
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


def test_is_purge_protected_detects_deleted_vault_purge_block():
    exc = subprocess.CalledProcessError(
        1,
        ["az", "keyvault", "purge"],
        output="",
        stderr="(MethodNotAllowed) Operation 'DeletedVaultPurge' is not allowed.",
    )
    assert is_purge_protected(exc) is True


def test_is_purge_protected_false_for_not_found():
    exc = subprocess.CalledProcessError(
        3, ["az", "keyvault", "purge"], output="", stderr="Vault not found"
    )
    assert is_purge_protected(exc) is False


def test_already_absent_result_wording():
    assert ALREADY_ABSENT_RESULT == "not-found (already absent)"


# ---------------------------------------------------------------------------
# group_tags / guarded_group_delete (tag guard)
# ---------------------------------------------------------------------------


def _show(stdout="", returncode=0, stderr=""):
    return MagicMock(returncode=returncode, stdout=stdout, stderr=stderr)


def _dsf_tags(**extra):
    tags = {"project": "dark-software-factory", "managed-by": "dsf", "product": "demo"}
    tags.update(extra)
    return json.dumps(tags)


def test_group_tags_returns_parsed_tags():
    def run(cmd, **kwargs):
        assert cmd[:3] == ["az", "group", "show"]
        return _show(stdout=_dsf_tags(component="backing-services"))

    tags = group_tags("rg-dsf-demo", run)
    assert tags == {
        "project": "dark-software-factory",
        "managed-by": "dsf",
        "product": "demo",
        "component": "backing-services",
    }


def test_group_tags_returns_none_when_absent():
    def run(cmd, **kwargs):
        return _show(returncode=3, stderr="ResourceGroupNotFound: rg-dsf-demo")

    assert group_tags("rg-dsf-demo", run) is None


def test_group_tags_empty_dict_when_no_tags():
    def run(cmd, **kwargs):
        return _show(stdout="null")

    assert group_tags("rg-dsf-demo", run) == {}


def test_group_tags_raises_on_non_404_error():
    def run(cmd, **kwargs):
        return _show(returncode=1, stderr="AuthorizationFailed")

    with pytest.raises(subprocess.CalledProcessError):
        group_tags("rg-dsf-demo", run)


def test_guarded_group_delete_deletes_dsf_tagged_group():
    calls = []

    def run(cmd, **kwargs):
        calls.append(cmd)
        if cmd[:3] == ["az", "group", "show"]:
            return _show(stdout=_dsf_tags(component="backing-services"))
        return _show()

    assert guarded_group_delete("rg-dsf-demo", run) == "deleted"
    assert ["az", "group", "delete", "--name", "rg-dsf-demo", "--yes"] in calls


def test_guarded_group_delete_tolerates_absent_group():
    calls = []

    def run(cmd, **kwargs):
        calls.append(cmd)
        return _show(returncode=3, stderr="ResourceGroupNotFound")

    assert guarded_group_delete("rg-dsf-demo", run) == ALREADY_ABSENT_RESULT
    # The group never existed, so we must not have issued a delete.
    assert not any(cmd[:3] == ["az", "group", "delete"] for cmd in calls)


def test_guarded_group_delete_refuses_untagged_group():
    def run(cmd, **kwargs):
        if cmd[:3] == ["az", "group", "show"]:
            return _show(stdout=json.dumps({"project": "someone-else"}))
        raise AssertionError("delete must not be attempted on a foreign group")

    with pytest.raises(ForeignResourceGroupError):
        guarded_group_delete("rg-dsf-demo", run)


def test_guarded_group_delete_refuses_wrong_managed_by_value():
    def run(cmd, **kwargs):
        if cmd[:3] == ["az", "group", "show"]:
            return _show(stdout=json.dumps({"managed-by": "terraform"}))
        raise AssertionError("delete must not be attempted on a foreign group")

    with pytest.raises(ForeignResourceGroupError):
        guarded_group_delete("rg-dsf-demo", run)


def test_guarded_group_delete_tolerates_delete_race_404():
    def run(cmd, **kwargs):
        if cmd[:3] == ["az", "group", "show"]:
            return _show(stdout=_dsf_tags())
        raise subprocess.CalledProcessError(
            3, cmd, output="", stderr="ResourceGroupNotFound"
        )

    assert guarded_group_delete("rg-dsf-demo", run) == ALREADY_ABSENT_RESULT


# ---------------------------------------------------------------------------
# AzureTeardown
# ---------------------------------------------------------------------------


def _ok(stdout: str = "") -> MagicMock:
    return MagicMock(returncode=0, stdout=stdout, stderr="")


def test_delete_group_delegates_to_guarded_delete():
    """AzureTeardown.delete_group applies the managed-by=dsf tag guard."""

    def run(cmd, **kwargs):
        if cmd[:3] == ["az", "group", "show"]:
            return _show(stdout=_dsf_tags())
        return _ok()

    assert AzureTeardown(run).delete_group("rg-live") == "deleted"


def test_delete_group_skips_when_absent():
    def run(cmd, **kwargs):
        return _show(returncode=3, stderr="ResourceGroupNotFound")

    assert AzureTeardown(run).delete_group("rg-gone") == ALREADY_ABSENT_RESULT


def test_delete_group_refuses_foreign_group():
    def run(cmd, **kwargs):
        if cmd[:3] == ["az", "group", "show"]:
            return _show(stdout=json.dumps({"managed-by": "terraform"}))
        raise AssertionError("delete must not be attempted on a foreign group")

    with pytest.raises(ForeignResourceGroupError):
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
    # `az role assignment delete` rejects --assignee-principal-type (create-only flag).
    assert all("--assignee-principal-type" not in cmd for cmd in role_deletes)
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
