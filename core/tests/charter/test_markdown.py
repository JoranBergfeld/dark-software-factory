from __future__ import annotations

import pytest

from dsf.charter.markdown import (
    CharterParseError,
    git_blob_sha,
    parse_charter,
    render_charter,
)
from dsf.contracts.charter import Charter


def _full_charter() -> Charter:
    return Charter(
        product="alpha",
        vision="Make alpha delightful.",
        target_users="Data analysts at SMBs.",
        goals=["Cut p99 latency", "Ship weekly"],
        non_goals=["Build a mobile app"],
        success_metrics=["p99 < 200ms"],
        constraints="Stay within Azure.",
        glossary={"p99": "99th percentile latency"},
    )


def test_render_then_parse_roundtrips():
    original = _full_charter()
    text = render_charter(original)
    parsed = parse_charter(text, product="alpha")
    assert parsed == original


def test_render_starts_with_marker():
    text = render_charter(_full_charter())
    assert text.splitlines()[0] == "<!-- dsf:charter schema_version=1 -->"


def test_empty_optional_sections_roundtrip():
    c = Charter(
        product="alpha",
        vision="V",
        target_users="U",
        goals=["g"],
        success_metrics=["m"],
    )
    assert parse_charter(render_charter(c), product="alpha") == c


def test_missing_required_section_raises():
    text = render_charter(_full_charter()).replace("## Goals\n- Cut p99 latency\n- Ship weekly", "")
    with pytest.raises(CharterParseError) as exc:
        parse_charter(text, product="alpha")
    assert "Goals" in str(exc.value)


def test_duplicate_section_raises():
    text = render_charter(_full_charter()) + "\n## Vision\nDuplicate.\n"
    with pytest.raises(CharterParseError) as exc:
        parse_charter(text, product="alpha")
    assert "duplicate" in str(exc.value).lower()


def test_unknown_section_raises():
    text = render_charter(_full_charter()).replace(
        "Make alpha delightful.", "Make alpha delightful.\n## Background\nUnexpected."
    )
    with pytest.raises(CharterParseError) as exc:
        parse_charter(text, product="alpha")
    assert "unknown section" in str(exc.value).lower()


def test_merge_conflict_markers_rejected():
    text = render_charter(_full_charter()).replace(
        "Make alpha delightful.", "<<<<<<< HEAD\nMake alpha delightful.\n======="
    )
    with pytest.raises(CharterParseError) as exc:
        parse_charter(text, product="alpha")
    assert "merge" in str(exc.value).lower()


def test_empty_required_value_raises():
    c = Charter(product="alpha", vision="V", target_users="U", goals=["g"], success_metrics=["m"])
    text = render_charter(c).replace("## Vision\nV", "## Vision\n")
    with pytest.raises(CharterParseError) as exc:
        parse_charter(text, product="alpha")
    assert "Vision" in str(exc.value)


def test_malformed_glossary_entry_raises():
    c = _full_charter()
    text = render_charter(c).replace("- p99: 99th percentile latency", "- nope")
    with pytest.raises(CharterParseError) as exc:
        parse_charter(text, product="alpha")
    assert "glossary" in str(exc.value).lower()


def test_unsupported_schema_version_raises():
    text = render_charter(_full_charter()).replace("schema_version=1", "schema_version=2")
    with pytest.raises(CharterParseError) as exc:
        parse_charter(text, product="alpha")
    assert "schema_version" in str(exc.value)


def test_git_blob_sha_matches_git():
    # `printf 'hello\n' | git hash-object --stdin`
    assert git_blob_sha(b"hello\n") == "ce013625030ba8dba906f756967f9e9ca394464a"
