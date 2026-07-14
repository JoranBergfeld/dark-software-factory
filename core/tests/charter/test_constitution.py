from __future__ import annotations

from datetime import date

from dsf.charter.constitution import (
    CONSTITUTION_PATH,
    is_constitution_current,
    render_constitution,
)
from dsf.contracts.charter import Charter


def _charter(**over) -> Charter:
    base = dict(
        product="alpha",
        vision="Make billing painless.",
        target_users="Small-business owners.",
        goals=["Cut invoice time", "Reduce errors"],
        non_goals=["No payroll", "No tax filing"],
        success_metrics=["p50 invoice < 60s", "error rate < 1%"],
        constraints="Must run in the EU. PCI-DSS scope minimized.",
        glossary={"Invoice": "a billable document", "Dunning": "payment chasing"},
        source_sha="deadbeef",
        source_ref="main",
    )
    base.update(over)
    return Charter(**base)


def test_path_constant():
    assert CONSTITUTION_PATH == ".specify/memory/constitution.md"


def test_title_and_preamble_carry_product_vision_and_users():
    md = render_constitution(_charter(), today=date(2026, 1, 1))
    assert md.startswith("<!-- dsf:constitution")
    assert "# alpha Constitution" in md
    assert "Make billing painless." in md
    assert "Small-business owners." in md


def test_every_charter_list_field_lands_in_a_section():
    md = render_constitution(_charter(), today=date(2026, 1, 1))
    for needle in (
        "Cut invoice time",          # goal
        "No payroll",                # non-goal
        "p50 invoice < 60s",         # metric
        "Must run in the EU.",       # constraints verbatim
        "**Invoice**: a billable document",  # glossary
    ):
        assert needle in md


def test_footer_date_is_deterministic_via_today_seam():
    md = render_constitution(_charter(), today=date(2026, 1, 1))
    assert "**Ratified**: 2026-01-01" in md
    assert "**Last Amended**: 2026-01-01" in md


def test_marker_carries_charter_provenance():
    md = render_constitution(_charter(), today=date(2026, 1, 1))
    assert "source_ref=main" in md
    assert "source_sha=deadbeef" in md


def test_empty_optional_fields_render_cleanly():
    md = render_constitution(
        _charter(non_goals=[], glossary={}, constraints=""),
        today=date(2026, 1, 1),
    )
    assert "(none declared in the charter)" in md
    assert "(no shared vocabulary declared)" in md
    assert "No additional constraints declared in the charter." in md


def test_same_charter_renders_identically():
    a = render_constitution(_charter(), today=date(2026, 1, 1))
    b = render_constitution(_charter(), today=date(2026, 1, 1))
    assert a == b


def test_is_current_true_for_freshly_rendered():
    charter = _charter()
    assert is_constitution_current(render_constitution(charter), charter) is True


def test_is_current_ignores_ratified_footer_date():
    charter = _charter()
    early = render_constitution(charter, today=date(2026, 1, 1))
    later = render_constitution(charter, today=date(2026, 6, 30))
    assert is_constitution_current(early, charter) is True
    assert is_constitution_current(later, charter) is True


def test_is_current_false_on_sha_mismatch():
    on_main = render_constitution(_charter(source_sha="oldsha"))
    assert is_constitution_current(on_main, _charter(source_sha="newsha")) is False


def test_is_current_false_on_schema_mismatch():
    charter = _charter()
    text = render_constitution(charter).replace("schema_version=1", "schema_version=2")
    assert is_constitution_current(text, charter) is False


def test_is_current_false_on_ref_mismatch():
    on_main = render_constitution(_charter(source_ref="main"))
    assert is_constitution_current(on_main, _charter(source_ref="release")) is False


def test_is_current_false_for_none_empty_or_headerless():
    charter = _charter()
    assert is_constitution_current(None, charter) is False
    assert is_constitution_current("", charter) is False
    assert is_constitution_current("# just a doc, no marker", charter) is False


def test_is_current_true_when_ref_and_sha_unknown_roundtrip():
    charter = _charter(source_sha=None, source_ref=None)
    assert is_constitution_current(render_constitution(charter), charter) is True
