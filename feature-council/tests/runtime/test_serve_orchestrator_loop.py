"""Tests for ``serve-orchestrator``'s continuous sweep loop (the deployed mode).

The deployed Container App runs
``python -m dsf.runtime.control serve-orchestrator --loop`` so the ACA revision stays
healthy: it sweeps the enabled sources on an interval and a single failed tick must never
tear the worker down.
"""

from __future__ import annotations

from dsf.contracts.enums import TriggerKind
from dsf.contracts.models import Run
from dsf.runtime.control import (
    _DEFAULT_SWEEP_INTERVAL,
    _resolve_sweep_interval,
    build_parser,
    run_orchestrator_loop,
)


def _fake_run() -> Run:
    return Run(trigger=TriggerKind.SCHEDULED)


def test_loop_runs_n_ticks_and_sleeps_between_them():
    ticks = 0
    slept: list[float] = []

    def run_tick() -> Run:
        nonlocal ticks
        ticks += 1
        return _fake_run()

    count = run_orchestrator_loop(
        services=None,
        interval=42,
        sleep=slept.append,
        run_tick=run_tick,
        max_iterations=3,
    )

    assert count == 3
    assert ticks == 3
    # sleeps happen between ticks, never after the final one
    assert slept == [42, 42]


def test_loop_survives_a_failing_tick_and_continues(capsys):
    calls = 0
    slept: list[float] = []

    def run_tick() -> Run:
        nonlocal calls
        calls += 1
        if calls == 1:
            raise RuntimeError("transient sweep failure")
        return _fake_run()

    count = run_orchestrator_loop(
        services=None,
        interval=1,
        sleep=slept.append,
        run_tick=run_tick,
        max_iterations=2,
    )

    assert count == 2  # the bad first tick did not abort the loop
    assert calls == 2
    assert slept == [1]
    err = capsys.readouterr().err
    assert "orchestrator tick failed" in err
    assert "transient sweep failure" in err


def test_loop_stops_cleanly_on_keyboard_interrupt():
    def run_tick() -> Run:
        raise KeyboardInterrupt

    # KeyboardInterrupt (SIGINT) breaks the loop without raising out.
    count = run_orchestrator_loop(
        services=None,
        interval=1,
        sleep=lambda _s: None,
        run_tick=run_tick,
        max_iterations=None,
    )
    assert count == 0


def test_resolve_sweep_interval_precedence(monkeypatch):
    monkeypatch.delenv("DSF_SWEEP_INTERVAL", raising=False)
    # explicit wins
    assert _resolve_sweep_interval(15) == 15
    # env used when no explicit
    monkeypatch.setenv("DSF_SWEEP_INTERVAL", "120")
    assert _resolve_sweep_interval(None) == 120
    # explicit still overrides env
    assert _resolve_sweep_interval(7) == 7
    # invalid env falls back to the default
    monkeypatch.setenv("DSF_SWEEP_INTERVAL", "not-a-number")
    assert _resolve_sweep_interval(None) == _DEFAULT_SWEEP_INTERVAL
    # unset env falls back to the default
    monkeypatch.delenv("DSF_SWEEP_INTERVAL", raising=False)
    assert _resolve_sweep_interval(None) == _DEFAULT_SWEEP_INTERVAL
    # values are floored at 1 second
    assert _resolve_sweep_interval(0) == 1


def test_serve_orchestrator_parser_wires_loop_and_interval():
    args = build_parser().parse_args(["serve-orchestrator"])
    assert args.loop is False
    assert args.interval is None

    args = build_parser().parse_args(
        ["serve-orchestrator", "--loop", "--interval", "5"]
    )
    assert args.loop is True
    assert args.interval == 5
