"""Tests for Azure name-prefix derivation."""

from __future__ import annotations

import pytest

from dsf.instance.naming import make_name_prefix


def test_make_name_prefix_appends_token_and_substrings():
    assert make_name_prefix("microproduct", token="wxyz") == "microprowxyz"


def test_make_name_prefix_sanitizes_to_lowercase_alnum():
    assert make_name_prefix("My-Cool_Product!", token="ab12") == "mycoolprab12"


def test_make_name_prefix_caps_total_length_at_12():
    out = make_name_prefix("averylongproductname", token="qrst")
    assert out == "averylonqrst"
    assert len(out) == 12


def test_make_name_prefix_random_token_starts_with_letter_and_varies():
    a = make_name_prefix("demo")
    b = make_name_prefix("demo")
    assert a[0].isalpha()
    assert a.startswith("demo")
    assert len(a) == 8
    assert a != b  # random 4-char token


def test_make_name_prefix_rejects_base_without_letters():
    with pytest.raises(ValueError):
        make_name_prefix("___")


def test_make_name_prefix_rejects_leading_digit():
    with pytest.raises(ValueError):
        make_name_prefix("1demo")
