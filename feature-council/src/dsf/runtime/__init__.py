"""Orchestrator runtime image package.

The feature-council orchestrator runs as a long-lived worker — an Azure Container
App in the product's resource group (ADR 0004). The image is built from the sibling
``Dockerfile`` and started via ``python -m dsf.runtime.control serve-orchestrator``.
"""

from __future__ import annotations
