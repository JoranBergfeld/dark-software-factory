"""Eval runner + CI gate (plan Task 8.1).

Runs the dry-run conveyor over the golden set and scores each case with the
three evaluators, then aggregates mean metrics. The ``--gate`` entrypoint fails
(non-zero exit) when any aggregate metric falls below its threshold, so CI can
block merges that regress grounding, routing, or verdict accuracy.

Run as a module::

    python -m dsf.evals.runner --gate

Golden case shape (``golden/cases.json``)::

    {
      "id": str,
      "name": str,
      "signal_payload": {"product_hints": [...], "source_kinds": [...], "text": ...},
      "config_overrides": {"<flag>": bool, ...},   # optional, applied via set_flag
      "setup": {"seed_debounce": bool,              # optional test scaffolding
              "seed_ungrounded_proposal": bool, # skip S3, inject synthetic-evidence proposal
              "seed_duplicate_proposal": bool}, # pre-seed proposal texts for veto test
      "expectations": {
        "expected_product": str | null,
        "expect_filed": bool,
        "must_be_grounded": bool
      }
    }
"""

from __future__ import annotations

import argparse
import asyncio
import json
import sys
from collections.abc import Callable
from pathlib import Path
from typing import TYPE_CHECKING, Any

from dsf.container import build_services
from dsf.contracts.enums import TriggerKind
from dsf.contracts.models import Run
from dsf.evals.evaluators import groundedness, routing_accuracy, verdict_match, veto_accuracy
from dsf.orchestrator.blackboard import Blackboard
from dsf.orchestrator.conveyor import run_line
from dsf.orchestrator.stations.s1_triage import SIGNAL_KIND

if TYPE_CHECKING:
    from dsf.container import Services

#: Default golden set location, alongside this module.
GOLDEN_PATH = Path(__file__).resolve().parent / "golden" / "cases.json"

#: The four metric keys produced for every case and aggregated for the gate.
METRIC_KEYS = ("groundedness", "routing_accuracy", "verdict_match", "veto_accuracy")

#: Minimum aggregate score per metric for the gate to pass.
GATE_THRESHOLDS: dict[str, float] = {
    "groundedness": 0.99,
    "routing_accuracy": 0.95,  # adversarial case requires word-boundary routing
    "verdict_match": 0.85,    # adversarial debounce burst + existing case
    "veto_accuracy": 0.95,    # adversarial duplicate veto case
}

ServicesFactory = Callable[[str], "Services"]


def load_cases(path: str | Path | None = None) -> list[dict[str, Any]]:
    """Load the golden cases list from ``path`` (defaults to the bundled set)."""
    target = Path(path) if path is not None else GOLDEN_PATH
    return json.loads(target.read_text(encoding="utf-8"))


def _build_run(case: dict[str, Any]) -> Run:
    """Build a dry-run SIGNAL run from a case's ``signal_payload``.

    Mirrors what ``dsf.triggers.ingestion.signal_to_run`` will do, but built
    locally to avoid coupling to a module being authored in parallel.
    """
    payload = dict(case.get("signal_payload", {}))
    hints = payload.get("product_hints") or []
    if isinstance(hints, str):
        hints = [hints]
    return Run(
        trigger=TriggerKind.SIGNAL,
        scope_product_hints=list(hints),
        signal_payload=payload,
        dry_run=True,
    )


def _apply_config_overrides(services: Services, case: dict[str, Any]) -> None:
    """Apply a case's ``config_overrides`` map onto the config store."""
    for flag, value in (case.get("config_overrides") or {}).items():
        services.config.set_flag(flag, bool(value))


async def _apply_setup(
    services: Services,
    case: dict[str, Any],
    run: Run,
    services_factory: ServicesFactory = build_services,
) -> None:
    """Apply optional test scaffolding declared on the case.

    ``seed_debounce`` pre-seeds an in-flight signal record matching this run's
    signal text so S1's debounce fires and the run is KILLED — the deterministic
    way to exercise a not-filed expectation through the real line.
    """
    setup = case.get("setup") or {}
    if setup.get("seed_debounce"):
        text = str(run.signal_payload.get("text", "")).strip()
        if text:
            await services.memory.put_record(
                {"kind": SIGNAL_KIND, "text": text, "run_id": "eval-seed"}
            )
    if setup.get("seed_ungrounded_proposal"):
        await _seed_ungrounded_proposal(services, run)
    if setup.get("seed_duplicate_proposal"):
        await _seed_duplicate_proposal(services, case, run, services_factory)



async def _seed_ungrounded_proposal(services: Services, run: Run) -> None:
    """Inject an adversarial proposal with a synthetic evidence id, skipping S3.

    Marks the S3 synthesis checkpoint as complete so the conveyor skips that
    station, then plants a single :class:`~dsf.contracts.models.Proposal` whose
    only ``evidence_id`` is a sentinel string that can never appear in
    ``run.evidence``. S4 should strip the synthetic id and kill the proposal;
    if S4 is broken the proposal survives, routes, and the groundedness
    evaluator catches the ungrounded evidence.
    """
    from dsf.contracts.enums import ProposalKind
    from dsf.contracts.models import Proposal
    from dsf.orchestrator.blackboard import Blackboard
    from dsf.orchestrator.stations import s3_synthesis

    bb = Blackboard(services.memory)
    # Skip S3 so it cannot overwrite this pre-seeded proposal.
    await bb.checkpoint(run.id, s3_synthesis.STATION)
    hint = (run.scope_product_hints or ["microbi"])[0]
    synthetic = Proposal(
        run_id=run.id,
        kind=ProposalKind.FIX,
        title="Fix: adversarial ungrounded evidence test",
        problem="Eval harness: proposal carrying a sentinel synthetic evidence id.",
        proposed_change="No real change -- eval harness adversarial case only.",
        product=hint,
        evidence_ids=["adversarial-synthetic-evidence-id-000"],
        confidence=0.8,
    )
    await bb.save_proposals(run.id, [synthetic])


async def _seed_duplicate_proposal(
    services: Services,
    case: dict[str, Any],
    run: Run,
    services_factory: ServicesFactory,
) -> None:
    """Pre-seed proposal texts so the council duplication critic vetoes them.

    Runs a mini S1+S2+S3 pipeline on a fresh services instance to discover the
    exact proposal texts this run will produce, then records them as
    ``proposal`` kind records in the main services memory.  When the real run
    reaches the council, the duplication critic finds near-identical records
    (token overlap 1.0) and issues a hard veto.
    """
    from dsf.contracts.enums import RunStatus
    from dsf.council.critics.duplication import RECORD_KIND as _PROPOSAL_KIND
    from dsf.orchestrator.blackboard import Blackboard
    from dsf.orchestrator.stations import s1_triage, s2_investigation, s3_synthesis

    mini_services = services_factory("local")
    _apply_config_overrides(mini_services, case)
    mini_run = _build_run(case)
    mini_run = await s1_triage.run(mini_run, mini_services)
    if mini_run.status == RunStatus.KILLED:
        return  # debounce fired on the mini run -- nothing to seed
    mini_run = await s2_investigation.run(mini_run, mini_services)
    mini_run = await s3_synthesis.run(mini_run, mini_services)
    mini_bb = Blackboard(mini_services.memory)
    mini_proposals = await mini_bb.load_proposals(mini_run.id)
    for p in mini_proposals:
        await services.memory.put_record(
            {"kind": _PROPOSAL_KIND, "text": f"{p.title} {p.problem}"}
        )


async def run_case(
    case: dict[str, Any],
    services_factory: ServicesFactory = build_services,
) -> dict[str, Any]:
    """Run one golden case through the dry-run line and score it.

    Returns ``{"id", "name", "status", "metrics": {<3 keys>}}``.
    """
    services = services_factory("local")
    _apply_config_overrides(services, case)

    run = _build_run(case)
    await _apply_setup(services, case, run, services_factory)

    result = await run_line(run, services)

    blackboard = Blackboard(services.memory)
    issues = await blackboard.load_issues(result.id)
    proposals = await blackboard.load_proposals(result.id)
    verdicts = await blackboard.load_verdicts(result.id)

    expectations = case.get("expectations", {})
    metrics = {
        "groundedness": groundedness(result, issues, proposals),
        "routing_accuracy": routing_accuracy(
            issues, expectations.get("expected_product")
        ),
        "verdict_match": verdict_match(
            result, bool(expectations.get("expect_filed", True))
        ),
        "veto_accuracy": veto_accuracy(
            verdicts, bool(expectations.get("must_veto", False))
        ),
    }

    return {
        "id": case.get("id"),
        "name": case.get("name"),
        "status": result.status.value,
        "metrics": metrics,
    }


def _aggregate(case_results: list[dict[str, Any]]) -> dict[str, float]:
    """Mean each metric across all case results (0.0 for an empty suite)."""
    if not case_results:
        return {key: 0.0 for key in METRIC_KEYS}
    aggregate: dict[str, float] = {}
    for key in METRIC_KEYS:
        total = sum(r["metrics"][key] for r in case_results)
        aggregate[key] = total / len(case_results)
    return aggregate


async def run_suite(
    path: str | Path | None = None,
    services_factory: ServicesFactory = build_services,
) -> dict[str, Any]:
    """Run every golden case and aggregate mean metrics.

    Returns ``{"cases": [<per-case result>...], "metrics": {<aggregate>}}``.
    """
    cases = load_cases(path)
    case_results = [await run_case(case, services_factory) for case in cases]
    return {"cases": case_results, "metrics": _aggregate(case_results)}


def _format_summary(result: dict[str, Any], failures: list[str]) -> str:
    """Render a human-readable gate summary."""
    lines = ["Eval suite summary:", ""]
    for case in result["cases"]:
        m = case["metrics"]
        lines.append(
            f"  - {case['id']} [{case['status']}]: "
            f"groundedness={m['groundedness']:.3f} "
            f"routing_accuracy={m['routing_accuracy']:.3f} "
            f"verdict_match={m['verdict_match']:.3f} "
            f"veto_accuracy={m.get('veto_accuracy', 1.0):.3f}"
        )
    lines.append("")
    lines.append("Aggregate metrics (threshold):")
    for key in METRIC_KEYS:
        value = result["metrics"][key]
        thr = GATE_THRESHOLDS[key]
        status = "FAIL" if key in failures else "ok"
        lines.append(f"  {key:>16}: {value:.3f}  (>= {thr:.2f})  [{status}]")
    lines.append("")
    if failures:
        lines.append(f"GATE FAILED: {', '.join(failures)} below threshold.")
    else:
        lines.append("GATE PASSED: all aggregate metrics meet thresholds.")
    return "\n".join(lines)


def gate(result: dict[str, Any]) -> int:
    """Evaluate gate logic over a suite ``result`` dict.

    Returns ``0`` when every aggregate metric meets its threshold, else a
    non-zero count of failing metrics. Pure: it inspects ``result["metrics"]``
    only, so tests can call it directly with a fabricated low-metric dict.
    """
    metrics = result.get("metrics", {})
    failures = [
        key
        for key, threshold in GATE_THRESHOLDS.items()
        if float(metrics.get(key, 0.0)) < threshold
    ]
    return len(failures)


def _gate_failures(result: dict[str, Any]) -> list[str]:
    """Names of metrics below threshold (for the printed summary)."""
    metrics = result.get("metrics", {})
    return [
        key
        for key, threshold in GATE_THRESHOLDS.items()
        if float(metrics.get(key, 0.0)) < threshold
    ]


def main(argv: list[str] | None = None) -> int:
    """CLI entrypoint. With ``--gate``, exit non-zero on metric regression."""
    parser = argparse.ArgumentParser(prog="dsf.evals.runner")
    parser.add_argument(
        "--gate",
        action="store_true",
        help="Run the golden suite and fail (non-zero) on any sub-threshold metric.",
    )
    parser.add_argument(
        "--golden",
        default=None,
        help="Path to a golden cases JSON file (defaults to the bundled set).",
    )
    args = parser.parse_args(argv)

    result = asyncio.run(run_suite(args.golden))
    failures = _gate_failures(result)
    print(_format_summary(result, failures))

    if args.gate:
        return gate(result)
    return 0


__all__ = [
    "GATE_THRESHOLDS",
    "GOLDEN_PATH",
    "METRIC_KEYS",
    "gate",
    "load_cases",
    "main",
    "run_case",
    "run_suite",
]


if __name__ == "__main__":
    sys.exit(main())
