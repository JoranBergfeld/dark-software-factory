"""Guard the frozen Python parity baseline used by the .NET migration."""

from __future__ import annotations

import json
from pathlib import Path

ROOT = Path(__file__).resolve().parents[3]
BASELINE = ROOT / "parity" / "baseline"
EVIDENCE = BASELINE / "evidence"

REQUIRED_SURFACES = {
    "dsf bootstrap",
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
    for name in (
        "commands",
        "dry-run-plans",
        "machine-readable-outputs",
        "schemas",
        "request-shapes",
        "persisted-records",
    ):
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


def test_parser_surface_snapshot_covers_bootstrap_command() -> None:
    snapshot = _load_json("evidence/commands/dsf-parser-surface-snapshot.json")
    namespaces = {
        case["namespace"]["command"]: case["namespace"]
        for case in snapshot["cases"]
        if "command" in case["namespace"]
    }

    assert namespaces["bootstrap"] == {
        "app_name": "dsf-demo-app",
        "appconfig_name": "appcs-dsf-owner",
        "command": "bootstrap",
        "keyvault_name": "kv-dsf-owner",
        "location": "swedencentral",
        "resource_group": "rg-dsf-app",
    }


def test_dry_run_plan_evidence_kind_only_points_at_successful_plans() -> None:
    matrix = _load_json("matrix.json")

    for entry in matrix["surfaces"]:
        if "dry-run-plan" not in entry["evidence_kinds"]:
            continue

        plan_paths = [
            evidence_path
            for evidence_path in entry["evidence"]
            if evidence_path.startswith("evidence/dry-run-plans/")
        ]
        assert plan_paths, entry["surface"]

        for plan_path in plan_paths:
            plan = json.loads((BASELINE / plan_path).read_text(encoding="utf-8"))
            assert plan["authority"] == "authoritative"
            plan_record = plan.get("plan") or plan["manifest"]["plan"]
            assert plan_record["steps"], plan_path


def test_machine_readable_output_rows_link_captured_payloads() -> None:
    matrix = _load_json("matrix.json")

    for entry in matrix["surfaces"]:
        if "machine-readable-output" not in entry["evidence_kinds"]:
            continue

        output_paths = [
            evidence_path
            for evidence_path in entry["evidence"]
            if evidence_path.startswith("evidence/machine-readable-outputs/")
        ]
        command_paths = [
            evidence_path
            for evidence_path in entry["evidence"]
            if evidence_path.startswith("evidence/commands/")
        ]
        assert output_paths or command_paths, entry["surface"]

        has_captured_payload = bool(output_paths)
        for command_path in command_paths:
            command = json.loads((BASELINE / command_path).read_text(encoding="utf-8"))
            stdout = command.get("stdout", "")
            if not stdout.strip():
                continue
            json.loads(stdout)
            has_captured_payload = True
        assert has_captured_payload, entry["surface"]

        for output_path in output_paths:
            payload = json.loads((BASELINE / output_path).read_text(encoding="utf-8"))
            assert payload["authority"] == "authoritative"
            assert "response" in payload or "stdout_json" in payload


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
