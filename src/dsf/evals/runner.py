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
      "setup": {"seed_debounce": bool},            # optional test scaffolding
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
from dsf.evals.evaluators import groundedness, routing_accuracy, verdict_match
from dsf.orchestrator.blackboard import Blackboard
from dsf.orchestrator.conveyor import run_line
from dsf.orchestrator.stations.s1_triage import SIGNAL_KIND

if TYPE_CHECKING:
    from dsf.container import Services

#: Default golden set location, alongside this module.
GOLDEN_PATH = Path(__file__).resolve().parent / "golden" / "cases.json"

#: The three metric keys produced for every case and aggregated for the gate.
METRIC_KEYS = ("groundedness", "routing_accuracy", "verdict_match")

#: Minimum aggregate score per metric for the gate to pass.
GATE_THRESHOLDS: dict[str, float] = {
    "groundedness": 0.99,
    "routing_accuracy": 0.8,
    "verdict_match": 0.8,
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


async def _apply_setup(services: Services, case: dict[str, Any], run: Run) -> None:
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
    await _apply_setup(services, case, run)

    result = await run_line(run, services)

    blackboard = Blackboard(services.memory)
    issues = await blackboard.load_issues(result.id)
    proposals = await blackboard.load_proposals(result.id)

    expectations = case.get("expectations", {})
    metrics = {
        "groundedness": groundedness(result, issues, proposals),
        "routing_accuracy": routing_accuracy(
            issues, expectations.get("expected_product")
        ),
        "verdict_match": verdict_match(
            result, bool(expectations.get("expect_filed", True))
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
            f"verdict_match={m['verdict_match']:.3f}"
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
