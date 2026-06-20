"""Triggers — turn scheduled sweeps into Runs.

DSF is pull-only: the orchestrator gets work by sweeping source agents on a
schedule. Public surface:

* :func:`dsf.triggers.scheduler.sweep` / ``run_sweep`` — scheduled run builder.
* :data:`dsf.triggers.app.app` — FastAPI app exposing a liveness probe.
"""

from __future__ import annotations
