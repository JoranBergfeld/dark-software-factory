"""DSF factory command-line entry point.

The ``dsf`` console script (:mod:`dsf.cli.factory`) creates/manages product
instances from the template (``dsf new``; future
``status``/``upgrade``/``destroy``).

The runtime control module (:mod:`dsf.runtime.control`, run as
``python -m dsf.runtime.control`` and fronted by ``dsf``'s runtime verbs) lives
in the feature-council member (see ADR 0010).
"""
