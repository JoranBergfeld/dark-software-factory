"""Command-line entrypoint: ``dsf run|sweep|serve-agent|control-center``.

This is a skeleton — full conveyor/agent/UI wiring arrives in later phases.
It stays importable and never crashes on a well-formed invocation.
"""

from __future__ import annotations

import argparse
import json
import sys
from pathlib import Path

from dsf.container import build_services


def _cmd_run(args: argparse.Namespace) -> int:
    """Handle ``dsf run`` (stub)."""
    services = build_services(args.mode)
    signal: dict = {}
    if args.signal:
        path = Path(args.signal)
        if not path.exists():
            print(f"signal file not found: {path}", file=sys.stderr)
            return 1
        signal = json.loads(path.read_text(encoding="utf-8"))
    dry_run = args.dry_run or services.config.is_enabled("dry_run")
    print(
        f"[dsf] run (mode={services.mode}, dry_run={dry_run}) "
        f"loaded signal with {len(signal)} top-level key(s); "
        "conveyor wiring lands in a later phase."
    )
    return 0


def _cmd_sweep(args: argparse.Namespace) -> int:
    """Handle ``dsf sweep`` (stub)."""
    services = build_services(args.mode)
    print(f"[dsf] sweep (mode={services.mode}); scheduled sweep wiring is a later phase.")
    return 0


def _cmd_serve_agent(args: argparse.Namespace) -> int:
    """Handle ``dsf serve-agent`` (stub)."""
    print(f"[dsf] serve-agent kind={args.kind}; A2A server wiring is a later phase.")
    return 0


def _cmd_control_center(args: argparse.Namespace) -> int:
    """Handle ``dsf control-center`` (stub)."""
    print("[dsf] control-center; web UI wiring is a later phase.")
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

    p_serve = sub.add_parser("serve-agent", help="serve a source agent over A2A")
    p_serve.add_argument("--kind", default="sentry", help="source agent kind")
    p_serve.set_defaults(func=_cmd_serve_agent)

    p_cc = sub.add_parser("control-center", help="serve the control center UI")
    p_cc.set_defaults(func=_cmd_control_center)

    return parser


def main(argv: list[str] | None = None) -> int:
    """Parse args and dispatch. Returns a process exit code."""
    parser = build_parser()
    args = parser.parse_args(argv)
    return args.func(args)


if __name__ == "__main__":
    raise SystemExit(main())
