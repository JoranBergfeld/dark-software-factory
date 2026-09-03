"""Guard the frozen Python parity baseline used by the .NET migration."""

from __future__ import annotations

import json
from pathlib import Path

ROOT = Path(__file__).resolve().parents[3]
BASELINE = ROOT / "parity" / "baseline"
EVIDENCE = BASELINE / "evidence"

REQUIRED_SURFACES = {
    "dsf new",
    "dsf list",
    "dsf offboard",
    "dsf delete/deprovision",
    "dsf run",
    "dsf sweep",
    "dsf serve-orchestrator",
    "dsf serve-agent",
    "dsf charter init",
    "dsf charter sync",
    "dsf charter status",
    "dsf charter implement",
    "dsf charter watch",
    "dsf-control-center",
    "control-center GET /api/state",
    "control-center POST /toggle",
    "control-center POST /set-value",
}

REQUIRED_EVIDENCE_KINDS = {
    "command-behavior",
    "exit-behavior",
    "machine-readable-output",
    "dry-run-plan",
    "schema-snapshot",
    "request-shape",
    "persisted-record",
}

REQUIRED_SCHEMAS = {
    "AuditRecord.json",
    "CouncilVerdict.json",
    "CriticScore.json",
    "EvidenceItem.json",
    "Proposal.json",
    "Provenance.json",
    "RoutedIssue.json",
    "Run.json",
}


def _load_json(relative: str) -> object:
    return json.loads((BASELINE / relative).read_text(encoding="utf-8"))


def test_baseline_has_required_artifact_structure() -> None:
    assert (BASELINE / "README.md").is_file()
    assert (BASELINE / "matrix.md").is_file()
    assert (BASELINE / "matrix.json").is_file()
    for name in ("commands", "dry-run-plans", "schemas", "request-shapes", "persisted-records"):
        assert (EVIDENCE / name).is_dir(), name


def test_parity_matrix_covers_every_in_scope_surface_and_evidence_kind() -> None:
    matrix = _load_json("matrix.json")
    surfaces = {entry["surface"] for entry in matrix["surfaces"]}
    assert REQUIRED_SURFACES <= surfaces

    authoritative = [entry for entry in matrix["surfaces"] if entry["authority"] == "authoritative"]
    evidence_kinds = {kind for entry in authoritative for kind in entry["evidence_kinds"]}
    assert REQUIRED_EVIDENCE_KINDS <= evidence_kinds

    for entry in matrix["surfaces"]:
        assert entry["authority"] in {"authoritative", "non-authoritative", "deferred"}
        assert entry["evidence"], entry["surface"]
        for evidence_path in entry["evidence"]:
            assert (BASELINE / evidence_path).is_file(), (entry["surface"], evidence_path)


def test_parity_matrix_references_every_frozen_evidence_file() -> None:
    matrix = _load_json("matrix.json")
    referenced = {
        evidence_path
        for entry in matrix["surfaces"]
        for evidence_path in entry["evidence"]
    }
    frozen_evidence = {
        path.relative_to(BASELINE).as_posix()
        for path in EVIDENCE.rglob("*")
        if path.is_file()
    }

    assert frozen_evidence <= referenced


def test_command_evidence_is_self_contained_without_python_runtime() -> None:
    for path in sorted((EVIDENCE / "commands").glob("*.json")):
        evidence = json.loads(path.read_text(encoding="utf-8"))
        assert evidence["captured_at"] == "2026-09-03T08:24:03.665+02:00"
        assert evidence["argv"]
        assert isinstance(evidence["exit_code"], int)
        assert "stdout" in evidence
        assert "stderr" in evidence
        assert evidence["authority"] in {"authoritative", "non-authoritative", "deferred"}


def test_schema_snapshots_are_frozen_in_baseline() -> None:
    schema_names = {path.name for path in (EVIDENCE / "schemas").glob("*.json")}
    assert REQUIRED_SCHEMAS <= schema_names
    for schema_name in REQUIRED_SCHEMAS:
        snapshot = json.loads((EVIDENCE / "schemas" / schema_name).read_text(encoding="utf-8"))
        assert snapshot["title"] == schema_name.removesuffix(".json")
        assert snapshot["type"] == "object"
