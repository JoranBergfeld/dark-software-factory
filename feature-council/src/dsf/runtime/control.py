"""``dsfctl`` — operate a running instance's feature-council runtime.

``run``/``sweep`` execute the conveyor in-process. ``serve-agent``/
``serve-orchestrator`` launch the respective ASGI services via uvicorn. Every
command wires the real per-product service bundle via
:func:`dsf.container.build_services`, which requires the Azure runtime
environment (``DSF_PRODUCT`` plus the data-plane endpoints).

The Control Center web UI ships as its own ``dsf-control-center`` console script
(the ``dsf-control-center`` package), not as a ``dsfctl`` subcommand.
"""

from __future__ import annotations

import argparse
import asyncio
import json
import os
import sys
import time
from collections.abc import Callable
from pathlib import Path

from dsf.config.owner_index import runtime_env_for_product
from dsf.container import build_services
from dsf.contracts.enums import SourceKind, TriggerKind
from dsf.contracts.models import Run

#: Default seconds between sweeps when ``serve-orchestrator --loop`` runs without
#: an explicit ``--interval`` or ``DSF_SWEEP_INTERVAL``.
_DEFAULT_SWEEP_INTERVAL = 300


def _coerce_hints(payload: dict) -> list[str]:
    """Pull product hints from ``payload['product_hints']`` (str or list)."""
    value = payload.get("product_hints")
    if isinstance(value, str) and value.strip():
        return [value.strip()]
    if isinstance(value, list):
        return [str(v).strip() for v in value if str(v).strip()]
    return []


def _coerce_source_kinds(payload: dict) -> list[SourceKind]:
    """Map ``payload['source_kinds']`` to :class:`SourceKind`, dropping unknowns."""
    raw = payload.get("source_kinds")
    if isinstance(raw, str):
        raw = [raw]
    if not isinstance(raw, list):
        return []
    kinds: list[SourceKind] = []
    for entry in raw:
        try:
            kinds.append(SourceKind(str(entry).upper()))
        except ValueError:
            continue
    return kinds


def signal_to_run(payload: dict) -> Run:
    """Build a SIGNAL-triggered :class:`Run` from a manual ``--signal`` payload.

    Deterministic, no I/O: normalizes a webhook/alert-shaped payload into a
    :class:`~dsf.contracts.models.Run`. The whole payload is preserved on
    ``signal_payload`` so later stations can re-derive scope.

    * ``scope_product_hints`` <- ``payload['product_hints']`` (or ``[]``).
    * ``source_kinds`` <- ``payload['source_kinds']`` mapped to
      :class:`SourceKind` (unknown kinds dropped; missing -> ``[]``).

    The run executes by default. Dry-run is a user-invoked preview only, set via
    the ``dsfctl run --dry-run`` flag, not a payload field or system default.
    """
    payload = payload or {}
    return Run(
        trigger=TriggerKind.SIGNAL,
        signal_payload=dict(payload),
        scope_product_hints=_coerce_hints(payload),
        source_kinds=_coerce_source_kinds(payload),
        dry_run=False,
    )


def _print_run_summary(run) -> None:
    """Print a compact summary of a finished run."""
    print(f"[dsf] run {run.id} -> status={run.status.value} (dry_run={run.dry_run})")
    print(f"[dsf]   evidence={len(run.evidence)} proposals={len(run.proposals)}")
    for rec in run.audit:
        print(f"[dsf]   audit[{rec.station}] {rec.message}")


def _get_services(args: argparse.Namespace | None = None):
    """Build the real services bundle or exit cleanly on misconfiguration.

    With ``--product`` the env is resolved from the owner App Config index
    (endpoints + non-secret pointers) before wiring the real Azure adapters.
    """
    product = getattr(args, "product", None) if args is not None else None
    try:
        if product:
            return build_services(env=runtime_env_for_product(product))
        return build_services()
    except ValueError as exc:
        print(f"[dsf] error: {exc}", file=sys.stderr)
        sys.exit(1)


def _cmd_run(args: argparse.Namespace) -> int:
    """Run the intake line for one signal JSON file."""
    from dsf.orchestrator.conveyor import run_line

    services = _get_services(args)
    if not args.signal:
        print("--signal <path> is required for `run`", file=sys.stderr)
        return 1
    path = Path(args.signal)
    if not path.exists():
        print(f"signal file not found: {path}", file=sys.stderr)
        return 1
    payload = json.loads(path.read_text(encoding="utf-8"))
    run = signal_to_run(payload)
    if args.dry_run:
        run.dry_run = True
    final = asyncio.run(run_line(run, services))
    _print_run_summary(final)
    return 0


def _cmd_sweep(args: argparse.Namespace) -> int:
    """Run a scheduled sweep across enabled sources."""
    from dsf.triggers.scheduler import run_sweep

    services = _get_services(args)
    final = asyncio.run(run_sweep(services))
    _print_run_summary(final)
    return 0


def _resolve_sweep_interval(explicit: int | None) -> int:
    """Resolve the loop sweep interval: explicit ``--interval`` > ``DSF_SWEEP_INTERVAL``
    env > 300s. Never below 1 second."""
    if explicit is not None:
        return max(1, explicit)
    raw = os.environ.get("DSF_SWEEP_INTERVAL", "").strip()
    if raw:
        try:
            return max(1, int(raw))
        except ValueError:
            pass
    return _DEFAULT_SWEEP_INTERVAL


def run_orchestrator_loop(
    services,
    *,
    interval: float,
    sleep: Callable[[float], None] = time.sleep,
    run_tick: Callable[[], Run] | None = None,
    max_iterations: int | None = None,
) -> int:
    """Continuously run orchestrator sweep ticks, sleeping ``interval`` seconds
    between them.

    A single tick's exception is logged and swallowed so one bad sweep never
    tears down the long-lived worker (the deployed Container App keeps running
    and retries on the next interval). Returns the number of ticks attempted.
    ``max_iterations`` bounds the loop for tests; production passes ``None`` (run
    until the process is signalled).
    """
    if run_tick is None:
        from dsf.triggers.scheduler import run_orchestrator_tick

        def _tick() -> Run:
            return asyncio.run(run_orchestrator_tick(services))

        tick = _tick
    else:
        tick = run_tick

    count = 0
    while max_iterations is None or count < max_iterations:
        try:
            _print_run_summary(tick())
        except KeyboardInterrupt:
            break
        except Exception as exc:  # noqa: BLE001 - keep the worker alive across ticks
            print(f"[dsf] orchestrator tick failed: {exc}", file=sys.stderr, flush=True)
        count += 1
        if max_iterations is not None and count >= max_iterations:
            break
        try:
            sleep(interval)
        except KeyboardInterrupt:
            break
    return count


def _cmd_serve_orchestrator(args: argparse.Namespace) -> int:
    """Orchestrator worker: sweep the enabled source agents (DSF is pull-only).

    Without ``--loop`` this runs a single tick and exits (handy for local/dev and
    CI). With ``--loop`` (how the deployed Container App runs) it sweeps forever,
    sleeping ``--interval`` seconds (or ``DSF_SWEEP_INTERVAL``, default 300)
    between ticks and surviving per-tick errors.
    """
    services = _get_services(args)
    if getattr(args, "loop", False):
        run_orchestrator_loop(services, interval=_resolve_sweep_interval(args.interval))
        return 0
    from dsf.triggers.scheduler import run_orchestrator_tick

    swept = asyncio.run(run_orchestrator_tick(services))
    _print_run_summary(swept)
    return 0


def _cmd_serve_agent(args: argparse.Namespace) -> int:
    """Serve a source agent over A2A via uvicorn."""
    import uvicorn

    from dsf.agents.registry import app_path, serveable_agents

    target = app_path(args.kind)
    if target is None:
        choices = serveable_agents()
        print(f"unknown agent kind: {args.kind} (choices: {choices})", file=sys.stderr)
        return 1
    uvicorn.run(target, host=args.host, port=args.port)
    return 0


def build_parser() -> argparse.ArgumentParser:
    """Build the ``dsfctl`` parser with all runtime subcommands."""
    parser = argparse.ArgumentParser(
        prog="dsfctl",
        description="Dark Software Factory — instance control CLI (feature-council runtime)",
    )
    sub = parser.add_subparsers(dest="command", required=True)

    p_run = sub.add_parser("run", help="run the intake line for one signal")
    p_run.add_argument("--dry-run", action="store_true", help="run line, skip filing")
    p_run.add_argument("--signal", help="path to a signal JSON file")
    p_run.add_argument(
        "--product", help="resolve runtime env for this product from the owner index"
    )
    p_run.set_defaults(func=_cmd_run)

    p_sweep = sub.add_parser("sweep", help="run a scheduled sweep")
    p_sweep.add_argument(
        "--product", help="resolve runtime env for this product from the owner index"
    )
    p_sweep.set_defaults(func=_cmd_sweep)


    p_orch = sub.add_parser(
        "serve-orchestrator",
        help="run the orchestrator worker (sweep enabled sources; --loop to run continuously)",
    )
    p_orch.add_argument(
        "--loop",
        action="store_true",
        help="sweep continuously (the deployed runtime mode) instead of a single tick",
    )
    p_orch.add_argument(
        "--interval",
        type=int,
        default=None,
        help="seconds between sweeps in --loop mode (default: DSF_SWEEP_INTERVAL env, else 300)",
    )
    p_orch.add_argument(
        "--product", help="resolve runtime env for this product from the owner index"
    )
    p_orch.set_defaults(func=_cmd_serve_orchestrator)

    p_serve = sub.add_parser("serve-agent", help="serve a source agent over A2A")
    p_serve.add_argument("--kind", default="sentry", help="source agent kind")
    p_serve.add_argument("--host", default="0.0.0.0", help="bind host")
    p_serve.add_argument("--port", type=int, default=8080, help="bind port")
    p_serve.set_defaults(func=_cmd_serve_agent)

    return parser


def main(argv: list[str] | None = None) -> int:
    """Parse args and dispatch. Returns a process exit code."""
    parser = build_parser()
    args = parser.parse_args(argv)
    return args.func(args)


if __name__ == "__main__":
    raise SystemExit(main())
