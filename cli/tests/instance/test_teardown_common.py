"""Tests for the shared teardown not-found classifier."""

from __future__ import annotations

import subprocess

import pytest

from dsf.instance.teardown_common import (
    ALREADY_ABSENT_RESULT,
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
