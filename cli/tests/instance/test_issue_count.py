"""Tests for the open-handoff-issue count helper (KEDA metric source)."""

from __future__ import annotations

from dsf.instance.issue_count import open_handoff_issue_count


def test_counts_issues_in_gh_json_array():
    payload = '[{"number": 1}, {"number": 7}, {"number": 12}]'
    assert open_handoff_issue_count(payload) == 3


def test_empty_array_is_zero():
    assert open_handoff_issue_count("[]") == 0


def test_blank_or_whitespace_is_zero():
    assert open_handoff_issue_count("") == 0
    assert open_handoff_issue_count("   \n") == 0


def test_malformed_json_is_zero():
    assert open_handoff_issue_count("not json") == 0


def test_non_array_json_is_zero():
    assert open_handoff_issue_count('{"number": 1}') == 0
