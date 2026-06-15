"""Triggers & ingestion — turn scheduled sweeps and webhook signals into Runs.

Public surface:

* :func:`dsf.triggers.ingestion.signal_to_run` — webhook payload -> ``Run``.
* :func:`dsf.triggers.debounce.should_suppress` — suppress a repeat signal.
* :func:`dsf.triggers.scheduler.sweep` / ``run_sweep`` — scheduled run builder.
* :data:`dsf.triggers.app.app` — FastAPI app exposing ``POST /ingest``.
"""

from __future__ import annotations
