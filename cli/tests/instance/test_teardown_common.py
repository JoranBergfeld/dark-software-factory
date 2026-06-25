"""Tests for the shared teardown not-found classifier."""

from __future__ import annotations

import json
import subprocess
from unittest.mock import MagicMock

import pytest

from dsf.instance.teardown_common import (
    ALREADY_ABSENT_RESULT,
    ForeignResourceGroupError,
    group_tags,
    guarded_group_delete,
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
