"""Command-line entrypoint: ``dsf run|sweep|serve-agent|serve-orchestrator|control-center``.

``run`` and ``sweep`` execute the conveyor in-process (local fakes by default, fully
dry-run safe). ``serve-agent``/``serve-orchestrator``/``control-center`` launch the
respective ASGI services via uvicorn.
"""

from __future__ import annotations

import argparse
import asyncio
import json
import sys
from pathlib import Path

from dsf.container import build_services


def _print_run_summary(run) -> None:
    """Print a compact summary of a finished run."""
    print(f"[dsf] run {run.id} -> status={run.status.value} (dry_run={run.dry_run})")
    print(f"[dsf]   evidence={len(run.evidence)} proposals={len(run.proposals)}")
    for rec in run.audit:
        print(f"[dsf]   audit[{rec.station}] {rec.message}")


def _cmd_run(args: argparse.Namespace) -> int:
    """Run the intake line for one signal JSON file."""
    from dsf.orchestrator.conveyor import run_line
    from dsf.triggers.ingestion import signal_to_run

    services = build_services(args.mode)
    if not args.signal:
        print("--signal <path> is required for `run`", file=sys.stderr)
        return 1
    path = Path(args.signal)
    if not path.exists():
        print(f"signal file not found: {path}", file=sys.stderr)
        return 1
    payload = json.loads(path.read_text(encoding="utf-8"))
    run = signal_to_run(payload)
    if args.dry_run or services.config.is_enabled("dry_run"):
        run.dry_run = True
    final = asyncio.run(run_line(run, services))
    _print_run_summary(final)
    return 0


def _cmd_sweep(args: argparse.Namespace) -> int:
    """Run a scheduled sweep across enabled sources."""
    from dsf.triggers.scheduler import run_sweep

    services = build_services(args.mode)
    final = asyncio.run(run_sweep(services))
    _print_run_summary(final)
    return 0


def _cmd_serve_orchestrator(args: argparse.Namespace) -> int:
    """One-shot orchestrator worker (a real deployment would loop on a queue)."""
    from dsf.triggers.scheduler import run_sweep

    services = build_services(args.mode)
    final = asyncio.run(run_sweep(services))
    _print_run_summary(final)
    return 0


_AGENT_MODULES = {
    "sentry": "dsf.agents.sentry.main:app",
    "grafana": "dsf.agents.grafana.main:app",
    "foundryiq": "dsf.agents.foundryiq.main:app",
    "webiq": "dsf.agents.webiq.main:app",
    "tickets": "dsf.agents.tickets.main:app",
}


def _cmd_serve_agent(args: argparse.Namespace) -> int:
    """Serve a source agent over A2A via uvicorn."""
    import uvicorn

    target = _AGENT_MODULES.get(args.kind)
    if target is None:
        choices = sorted(_AGENT_MODULES)
        print(f"unknown agent kind: {args.kind} (choices: {choices})", file=sys.stderr)
        return 1
    uvicorn.run(target, host=args.host, port=args.port)
    return 0


def _cmd_control_center(args: argparse.Namespace) -> int:
    """Serve the Control Center web UI via uvicorn."""
    import uvicorn

    uvicorn.run("dsf.control_center.app:app", host=args.host, port=args.port)
    return 0


def build_parser() -> argparse.ArgumentParser:
    """Build the argparse parser with all subcommands."""
    parser = argparse.ArgumentParser(prog="dsf", description="Dark Software Factory CLI")
    parser.add_argument(
        "--mode",
        default="local",
        help="service mode: local (fakes) or azure (default: local)",
    )
    sub = parser.add_subparsers(dest="command", required=True)

    p_run = sub.add_parser("run", help="run the intake line for one signal")
    p_run.add_argument("--dry-run", action="store_true", help="run line, skip filing")
    p_run.add_argument("--signal", help="path to a signal JSON file")
    p_run.set_defaults(func=_cmd_run)

    p_sweep = sub.add_parser("sweep", help="run a scheduled sweep")
    p_sweep.set_defaults(func=_cmd_sweep)

    p_orch = sub.add_parser(
        "serve-orchestrator", help="run the orchestrator worker (one-shot sweep)"
    )
    p_orch.set_defaults(func=_cmd_serve_orchestrator)

    p_serve = sub.add_parser("serve-agent", help="serve a source agent over A2A")
    p_serve.add_argument("--kind", default="sentry", help="source agent kind")
    p_serve.add_argument("--host", default="0.0.0.0", help="bind host")
    p_serve.add_argument("--port", type=int, default=8080, help="bind port")
    p_serve.set_defaults(func=_cmd_serve_agent)

    p_cc = sub.add_parser("control-center", help="serve the control center UI")
    p_cc.add_argument("--host", default="0.0.0.0", help="bind host")
    p_cc.add_argument("--port", type=int, default=8081, help="bind port")
    p_cc.set_defaults(func=_cmd_control_center)

    return parser


def main(argv: list[str] | None = None) -> int:
    """Parse args and dispatch. Returns a process exit code."""
    parser = build_parser()
    args = parser.parse_args(argv)
    return args.func(args)


if __name__ == "__main__":
    raise SystemExit(main())
