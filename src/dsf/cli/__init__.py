"""DSF command-line entry points.

Two console scripts live here, both inside the single ``dsf`` package (ADR 0001):

* ``dsf``    (:mod:`dsf.cli.factory`) — create/manage product instances from the
  template (``dsf new``; future ``status``/``upgrade``/``destroy``).
* ``dsfctl`` (:mod:`dsf.cli.control`) — operate a running instance's feature-council
  runtime (``run``/``sweep``/``serve-orchestrator``/``serve-agent``/``control-center``).
"""
