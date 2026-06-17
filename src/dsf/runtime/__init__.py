"""Orchestrator runtime image package.

The feature-council orchestrator runs as a long-lived worker in the product's
runtime target (homelab docker compose today; ACA later). The image is built from
the sibling ``Dockerfile`` and started via ``dsf --mode azure serve-orchestrator``.
"""

from __future__ import annotations
