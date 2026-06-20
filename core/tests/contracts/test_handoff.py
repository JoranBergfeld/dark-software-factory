"""Tests for the well-known handoff/incident label contracts."""

from __future__ import annotations

from dsf.contracts.handoff import (
    HANDOFF_LABEL,
    INCIDENT_LABEL,
    INCIDENT_LABEL_COLOR,
    INCIDENT_LABEL_DESCRIPTION,
)


def test_incident_label_is_stable_marker():
    assert INCIDENT_LABEL == "incident"
    assert INCIDENT_LABEL != HANDOFF_LABEL


def test_incident_label_metadata_is_present():
    assert INCIDENT_LABEL_DESCRIPTION.strip()
    # 6-hex GitHub label color, no leading '#'.
    assert len(INCIDENT_LABEL_COLOR) == 6
    int(INCIDENT_LABEL_COLOR, 16)
