from __future__ import annotations

from datetime import UTC, datetime

from dsf.contracts.charter import Charter, StoredCharter
from dsf.contracts.enums import CharterStatus
from dsf.contracts.models import Proposal


def test_charter_defaults_and_fields():
    c = Charter(
        product="alpha",
        vision="V",
        target_users="U",
        goals=["g1"],
        success_metrics=["m1"],
    )
    assert c.schema_version == 1
    assert c.source_sha is None and c.source_ref is None
    assert c.non_goals == [] and c.constraints == "" and c.glossary == {}


def test_stored_charter_json_roundtrip():
    c = Charter(
        product="alpha",
        vision="V",
        target_users="U",
        goals=["g1"],
        success_metrics=["m1"],
    )
    s = StoredCharter(
        product="alpha",
        charter=c,
        status=CharterStatus.OK,
        last_synced_at=datetime(2026, 6, 23, tzinfo=UTC),
    )
    assert StoredCharter.model_validate(s.model_dump(mode="json")) == s


def test_stored_charter_missing_carries_no_charter():
    s = StoredCharter(product="alpha", charter=None, status=CharterStatus.MISSING, last_error="x")
    assert s.charter is None and s.status == CharterStatus.MISSING


def test_proposal_context_tags_default_empty():
    p = Proposal(run_id="r", kind="FIX", title="t", problem="p", proposed_change="c")
    assert p.context_tags == []
