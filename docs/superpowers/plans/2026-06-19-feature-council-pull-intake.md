# Plan 3: Feature council governed pull intake

- Status: Ready
- Date: 2026-06-19
- Implements: ADR 0011 (feature council deliberative redesign), the "Intake is a
  governed pull" decision. Builds on Plan 1 (validation jury) and Plan 2
  (deliberation council), both landed.
- Design detail:
  [`docs/superpowers/specs/2026-06-19-feature-council-deliberative-redesign-design.md`](../specs/2026-06-19-feature-council-deliberative-redesign-design.md)

## Why

ADR 0011 records: the synchronous push-into-the-pipeline path lets an external
source set the council's cadence, which is an ungoverned surface for a phase
whose job is deliberate decision-making. The fix: sources collect into a buffer,
and the council drains it on a schedule it owns. Event-driven urgency stays with
the SRE incident fast-path (ADR 0009). Debounce is retained.

Today `POST /ingest` (`triggers/app.py`) maps a webhook payload to a `Run` and
drives the whole conveyor synchronously in dry-run. This plan removes that
synchronous drive: `/ingest` becomes enqueue-only, and the scheduled worker
drains the buffer.

## Background the engineer needs

- **Current `/ingest`** (`feature-council/src/dsf/triggers/app.py:65-83`): pause
  check -> debounce (`should_suppress`) -> `record_signal` -> `signal_to_run` ->
  force `run.dry_run = True` -> `run_line` -> returns `{"run_id", "status"}`.
- **`/file`** (same file, 86-108): the deliberate human filing path. It does not
  debounce and does not force dry-run. It is **out of scope**: ADR 0011 removes
  the automated source *push*, not the explicit human invocation. Leave it as is.
- **Scheduled sweep** (`triggers/scheduler.py`): `sweep` builds a SCHEDULED run
  scoped to all enabled source kinds; `run_sweep` drives it through `run_line`.
  Returns a KILLED run when the SCHEDULED trigger is paused. Leave `sweep` and
  `run_sweep` unchanged; add a sibling drain function.
- **Orchestrator worker** (`runtime/control.py:71-78`, `_cmd_serve_orchestrator`,
  reached by `dsfctl --mode azure serve-orchestrator`): today calls `run_sweep`.
  This is the council-owned schedule tick.
- **Ports** live in `core/src/dsf/ports/__init__.py` as `typing.Protocol`s; each
  has an honest in-memory sibling in its domain module (`dsf.model`,
  `dsf.memory.store`, `dsf.config.store`). `build_services` (core/container.py)
  wires them per mode (`local`/`gh`/`azure`).
- **`Services`** (`core/src/dsf/container.py:65-77`) is a mutable dataclass; tests
  swap fields freely.
- **Import contracts** (`uv run lint-imports`, 4 kept): core must not import any
  application member. The new port and its in-memory impl therefore live in core
  and import nothing from feature-council. The buffer stores raw payload dicts
  only; the feature-council scheduler maps drained payloads to runs via
  `signal_to_run`, so the layering stays clean.

## Design decisions

- **Port shape is minimal: `enqueue` + `drain`.** The in-memory buffer is
  at-most-once: a drained batch that fails is not redelivered. That is acceptable
  here because sources re-emit (Sentry keeps firing) and debounce dedupes the
  repeat. The real adapter (Azure Service Bus, per the charter) will add
  lease/ack/dead-letter behind the same port. Modelling lease/ack now would be
  speculative (YAGNI); the port can grow when the Service Bus adapter lands, the
  same way the model/memory/config ports grew real Azure siblings later.
- **Each buffered signal drains to its own SIGNAL run.** A drain tick maps every
  queued payload through `signal_to_run` and runs each through `run_line`,
  mirroring the per-signal semantics `/ingest` had before. The source-kind
  SCHEDULED sweep stays a separate run.
- **Drained runs keep the old dry-run posture.** `/ingest` forced
  `run.dry_run = True` so a webhook could never file directly; `drain_signals`
  preserves that. Whether anything files is still governed by the maturity outcome
  (Plan 1) and the global `dry_run` config flag, unchanged by this plan.
- **SIGNAL pause leaves items buffered.** If the SIGNAL trigger is paused (the
  control-center kill switch) when a tick runs, `drain_signals` returns `[]`
  without draining, so the queued signals wait for intake to resume rather than
  being dropped. Enqueue at `/ingest` already refuses when SIGNAL is paused.

## File structure

New:
- `core/src/dsf/signals/__init__.py` - exports `InMemorySignalBuffer`.
- `core/src/dsf/signals/buffer.py` - `InMemorySignalBuffer`.
- `core/tests/signals/test_buffer.py` - buffer unit tests.

Modified:
- `core/src/dsf/ports/__init__.py` - add `SignalBuffer` Protocol + export.
- `core/src/dsf/container.py` - add `signals` to `Services`; wire all 3 modes.
- `core/tests/test_container.py` - assert the buffer is wired per mode.
- `feature-council/src/dsf/triggers/app.py` - `/ingest` enqueues.
- `feature-council/tests/triggers/test_app.py` - new queued contract.
- `feature-council/src/dsf/triggers/scheduler.py` - add `drain_signals`.
- `feature-council/tests/triggers/test_scheduler.py` - drain tests.
- `feature-council/src/dsf/runtime/control.py` - orchestrator tick drains + sweeps.
- `feature-council/tests/runtime/test_control.py` (or existing) - tick test.
- `docs/phases/feature-council.md`, `docs/adr/0011-...md` - record the landing.

## Task 1: Add the `SignalBuffer` port and in-memory buffer

### Step 1.1 (test): `core/tests/signals/test_buffer.py`

```python
"""InMemorySignalBuffer tests."""

from __future__ import annotations

from dsf.signals import InMemorySignalBuffer


async def test_enqueue_then_drain_returns_payloads_in_order():
    buf = InMemorySignalBuffer()
    await buf.enqueue({"text": "a"})
    await buf.enqueue({"text": "b"})

    drained = await buf.drain()
    assert drained == [{"text": "a"}, {"text": "b"}]


async def test_drain_clears_the_buffer():
    buf = InMemorySignalBuffer()
    await buf.enqueue({"text": "a"})

    assert await buf.drain() == [{"text": "a"}]
    # Second drain is empty: the first drain took ownership of the batch.
    assert await buf.drain() == []


async def test_drain_empty_returns_empty_list():
    buf = InMemorySignalBuffer()
    assert await buf.drain() == []


async def test_enqueue_copies_the_payload():
    buf = InMemorySignalBuffer()
    payload = {"text": "a"}
    await buf.enqueue(payload)
    payload["text"] = "mutated"

    # The buffer holds a snapshot, not a live reference to the caller's dict.
    assert await buf.drain() == [{"text": "a"}]
```

### Step 1.2 (impl): `core/src/dsf/signals/buffer.py`

```python
"""In-memory signal buffer - the honest offline SignalBuffer.

Sources enqueue accepted signal payloads; the scheduled council worker drains
them on its own cadence (the governed pull of ADR 0011). This implementation is
at-most-once: ``drain`` takes ownership of the pending batch and clears it, so a
batch that fails downstream is not redelivered. That is acceptable offline and at
the current maturity because sources re-emit and debounce dedupes the repeat; the
real Azure Service Bus adapter will add lease/ack/dead-letter behind this port.
"""

from __future__ import annotations


class InMemorySignalBuffer:
    """A simple FIFO queue of pending signal payloads."""

    def __init__(self) -> None:
        self._items: list[dict] = []

    async def enqueue(self, payload: dict) -> None:
        """Append a snapshot of ``payload`` to the pending queue."""
        self._items.append(dict(payload))

    async def drain(self) -> list[dict]:
        """Return all pending payloads and clear the queue (at-most-once)."""
        items = self._items
        self._items = []
        return items


__all__ = ["InMemorySignalBuffer"]
```

### Step 1.3 (impl): `core/src/dsf/signals/__init__.py`

```python
"""Signal buffer - the pull-intake queue between sources and the council."""

from __future__ import annotations

from dsf.signals.buffer import InMemorySignalBuffer

__all__ = ["InMemorySignalBuffer"]
```

### Step 1.4 (impl): add the port to `core/src/dsf/ports/__init__.py`

Add after the `Tracer` protocol:

```python
@runtime_checkable
class SignalBuffer(Protocol):
    """Pending-signal queue drained by the scheduled council sweep.

    Sources enqueue accepted signal payloads; the council pulls them on its own
    schedule (the governed pull of ADR 0011). The in-memory implementation is
    at-most-once; the real Azure Service Bus adapter adds lease/ack/dead-letter.
    """

    async def enqueue(self, payload: dict) -> None:
        """Append a signal payload to the pending queue."""
        ...

    async def drain(self) -> list[dict]:
        """Return all pending payloads and clear the queue."""
        ...
```

Add `"SignalBuffer"` to `__all__`.

### Step 1.5 (impl): wire into `Services` and `build_services` (core/container.py)

- Import: `from dsf.signals import InMemorySignalBuffer` and add `SignalBuffer`
  to the `from dsf.ports import (...)` block.
- Add a field to `Services`: `signals: SignalBuffer` (place it after `tracer`,
  before the optional `product`/`azure` fields so the dataclass keeps its
  required-then-default order).
- In each of the three mode branches (`local`, `gh`, `azure`) pass
  `signals=InMemorySignalBuffer()`. The azure branch keeps the in-memory buffer
  for now (the Service Bus adapter is a later SP, mirroring the model/memory seams
  that fall back to the in-memory sibling until their endpoint is wired).

### Step 1.6 (test): extend `core/tests/test_container.py`

Add a test that every supported mode wires a usable buffer. Check the existing
file for its mode-iteration helper and match its style. Example:

```python
async def test_local_services_wire_a_signal_buffer():
    services = build_services("local")
    await services.signals.enqueue({"text": "x"})
    assert await services.signals.drain() == [{"text": "x"}]
```

### Step 1.7: validate

```
uv run pytest core/tests/signals/ core/tests/test_container.py -q
uv run ruff check . && uv run lint-imports
```

Commit: `feat(signals): add SignalBuffer port and in-memory buffer`.

## Task 2: `/ingest` enqueues instead of driving the line

### Step 2.1 (test): update `feature-council/tests/triggers/test_app.py`

`test_ingest_sample_signal_runs_dry_run_line` asserted a `run_id` and a terminal
status from a synchronous run. The new contract is enqueue-only. Replace it:

```python
def test_ingest_enqueues_signal_and_does_not_run_the_line(
    client: TestClient, services: Services
) -> None:
    payload = json.loads(_FIXTURE.read_text(encoding="utf-8"))

    resp = client.post("/ingest", json=payload)

    assert resp.status_code == 200
    assert resp.json() == {"status": "queued"}
    # The line did not run synchronously: nothing was filed and the signal is
    # waiting in the buffer for the scheduled drain.
    assert services.github.calls == []
    import asyncio

    assert asyncio.run(services.signals.drain()) == [payload]
```

Keep `test_ingest_paused_signal_returns_paused` (still returns paused before
enqueue) and `test_ingest_duplicate_signal_suppressed` (suppression still happens
before enqueue, so the second post returns `{"status": "suppressed"}`). After the
change, assert the paused/suppressed cases enqueue nothing.

### Step 2.2 (impl): rewrite the `/ingest` handler (`triggers/app.py`)

```python
@app.post("/ingest")
async def ingest(
    payload: dict[str, Any] = _BODY,
    services: Services = _SERVICES,
) -> dict[str, Any]:
    """Enqueue a webhook signal for the scheduled council drain (governed pull).

    The synchronous push-into-the-pipeline path is gone (ADR 0011): an inbound
    source can no longer set the council's cadence. The signal is debounced and,
    if new, recorded and enqueued; the scheduled worker drains the buffer on the
    council's own schedule.
    """
    if triggers_paused(services.config, TriggerKind.SIGNAL):
        return {"status": "paused"}

    if await should_suppress(payload, services):
        return {"status": "suppressed"}

    await record_signal(payload, services)
    await services.signals.enqueue(payload)
    return {"status": "queued"}
```

Remove the now-unused imports (`run_line`, `signal_to_run`) from `app.py` if no
other handler uses them. `/file` still uses both, so they stay. Update the module
docstring to describe enqueue-only `/ingest`.

### Step 2.3: validate

```
uv run pytest feature-council/tests/triggers/test_app.py -q
uv run ruff check .
```

Commit: `feat(triggers): /ingest enqueues to the signal buffer (governed pull)`.

## Task 3: Drain the buffer on the schedule

### Step 3.1 (test): extend `feature-council/tests/triggers/test_scheduler.py`

```python
async def test_drain_signals_processes_each_buffered_payload() -> None:
    services = build_services("local")
    await services.signals.enqueue({"text": "alpha p99 high", "product_hints": ["alpha"]})
    await services.signals.enqueue({"text": "beta errors", "product_hints": ["beta"]})

    runs = await drain_signals(services)

    # One run per buffered signal, each advanced through the conveyor.
    assert len(runs) == 2
    assert all(r.trigger == TriggerKind.SIGNAL for r in runs)
    assert all(r.status != RunStatus.OPEN for r in runs)
    # Drained: the buffer is empty afterwards.
    assert await services.signals.drain() == []
    # Dry-run posture preserved: no real filing.
    assert services.github.calls == []


async def test_drain_signals_empty_buffer_returns_no_runs() -> None:
    services = build_services("local")
    assert await drain_signals(services) == []


async def test_drain_signals_paused_leaves_items_buffered() -> None:
    services = build_services("local")
    services.config.set_flag("trigger.SIGNAL.paused", True)
    await services.signals.enqueue({"text": "x"})

    runs = await drain_signals(services)

    assert runs == []
    # Items are not dropped while paused: they wait for intake to resume.
    assert await services.signals.drain() == [{"text": "x"}]
```

Add `drain_signals` to the import and `RunStatus`/`TriggerKind` if not present.

### Step 3.2 (impl): add `drain_signals` to `triggers/scheduler.py`

```python
async def drain_signals(services: Services) -> list[Run]:
    """Pull every buffered signal and run each through the conveyor in dry-run.

    This is the governed pull (ADR 0011): the scheduled worker drains the buffer
    on the council's own cadence. If the SIGNAL trigger is paused the buffer is
    left intact so queued signals wait for intake to resume rather than being
    dropped. Each payload is mapped with :func:`signal_to_run` and forced to
    dry-run, mirroring the pre-redesign ``/ingest`` posture; whether anything
    files is still governed by the maturity outcome and the global dry-run flag.
    """
    if triggers_paused(services.config, TriggerKind.SIGNAL):
        return []

    payloads = await services.signals.drain()
    if not payloads:
        return []

    from dsf.orchestrator.conveyor import run_line
    from dsf.triggers.ingestion import signal_to_run

    runs: list[Run] = []
    for payload in payloads:
        run = signal_to_run(payload)
        run.dry_run = True
        runs.append(await run_line(run, services))
    return runs
```

Add `drain_signals` to `__all__`.

### Step 3.3: validate

```
uv run pytest feature-council/tests/triggers/test_scheduler.py -q
uv run ruff check . && uv run lint-imports
```

Commit: `feat(triggers): drain the signal buffer on the scheduled tick`.

## Task 4: Wire the orchestrator worker to drain then sweep

### Step 4.1 (test): `feature-council/tests/runtime/`

Check the existing runtime test for `serve-orchestrator` (or `_cmd_serve_orchestrator`).
Add a test that a buffered signal is processed by the worker tick and the source
sweep still runs. If `_cmd_serve_orchestrator` only prints, test via a new
`run_orchestrator_tick(services)` helper that returns the runs, so the behavior is
assertable offline without capturing stdout:

```python
async def test_orchestrator_tick_drains_buffer_and_sweeps() -> None:
    services = build_services("local")
    await services.signals.enqueue({"text": "x", "product_hints": ["alpha"]})

    drained, swept = await run_orchestrator_tick(services)

    assert len(drained) == 1  # the buffered signal was processed
    assert swept.trigger == TriggerKind.SCHEDULED  # the source sweep still ran
    assert await services.signals.drain() == []
```

### Step 4.2 (impl): add `run_orchestrator_tick` to `triggers/scheduler.py`

```python
async def run_orchestrator_tick(services: Services) -> tuple[list[Run], Run]:
    """One council-owned tick: drain the pull buffer, then run the source sweep."""
    drained = await drain_signals(services)
    swept = await run_sweep(services)
    return drained, swept
```

Add it to `__all__`.

### Step 4.3 (impl): use it in `runtime/control.py`

`_cmd_serve_orchestrator` calls `run_orchestrator_tick`, prints a summary for the
sweep and for each drained run:

```python
def _cmd_serve_orchestrator(args: argparse.Namespace) -> int:
    """One-shot orchestrator worker: drain the signal buffer, then sweep sources."""
    from dsf.triggers.scheduler import run_orchestrator_tick

    services = _get_services(args.mode)
    drained, swept = asyncio.run(run_orchestrator_tick(services))
    for run in drained:
        _print_run_summary(run)
    _print_run_summary(swept)
    return 0
```

Leave `_cmd_sweep` as the source-only sweep (it is the explicit "sweep" command).

### Step 4.4: validate

```
uv run pytest feature-council/tests/runtime/ feature-council/tests/triggers/ -q
uv run ruff check . && uv run lint-imports
```

Commit: `feat(runtime): orchestrator tick drains the buffer before sweeping`.

## Task 5: Document the governed pull

- `docs/phases/feature-council.md`:
  - "Inputs and outputs": the parenthetical that says a push endpoint still runs
    next to the sweep becomes "landed": intake is now pull-only; `/ingest`
    enqueues and the scheduled worker drains the buffer; `/file` remains the
    deliberate human path.
  - "Where it lives": move the pull intake from "pending (Plan 3)" to landed, so
    the whole ADR 0011 redesign (Plans 1-3) is recorded as shipped.
- `docs/adr/0011-...md`: extend the consequences to record Plan 3 landed (the
  `SignalBuffer` port + in-memory buffer, enqueue-only `/ingest`, scheduled
  `drain_signals`, the at-most-once / Service-Bus-later decision).
- Check off this plan's definition of done.
- Verify humanizer-clean:
  `grep -nP "[\x{2014}\x{2013}\x{2018}\x{2019}\x{201C}\x{201D}]" <edited docs>`.

Commit: `docs(council): record governed pull intake landing (Plan 3)`.

## Definition of done

- [ ] Task 1 - `SignalBuffer` port + `InMemorySignalBuffer` + `Services` wiring + tests.
- [ ] Task 2 - `/ingest` enqueues; queued contract; app tests updated.
- [ ] Task 3 - `drain_signals` pulls + processes; SIGNAL-pause leaves buffered; tests.
- [ ] Task 4 - orchestrator tick drains then sweeps; tests.
- [ ] Task 5 - docs + ADR + plan check-off; humanizer-clean.
- [ ] Full gauntlet green: `uv run pytest -q`, `uv run ruff check .`,
  `uv run python -m dsf.evals.runner --gate`, `uv run lint-imports`.
