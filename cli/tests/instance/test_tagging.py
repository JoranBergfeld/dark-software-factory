"""Tests for the canonical Azure tag helpers."""

from __future__ import annotations

from dsf.instance.tagging import (
    MANAGED_BY_TAG,
    MANAGED_BY_VALUE,
    PROJECT_TAG_VALUE,
    canonical_tags,
    tag_cli_args,
)


def test_canonical_tags_full_set():
    assert canonical_tags("microbi", "backing-services") == {
        "project": "dark-software-factory",
        "managed-by": "dsf",
        "product": "microbi",
        "component": "backing-services",
    }


def test_canonical_tags_carries_managed_by_marker():
    tags = canonical_tags("demo", "sre-agent")
    assert tags[MANAGED_BY_TAG] == MANAGED_BY_VALUE
    assert tags["project"] == PROJECT_TAG_VALUE
    assert tags["product"] == "demo"
    assert tags["component"] == "sre-agent"


def test_tag_cli_args_formats_key_value_tokens():
    args = tag_cli_args(canonical_tags("demo", "backing-services"))
    assert args == [
        "project=dark-software-factory",
        "managed-by=dsf",
        "product=demo",
        "component=backing-services",
    ]


def test_tag_cli_args_empty():
    assert tag_cli_args({}) == []