"""The SRE agent — close the production-incident loop into the coding squad.

The agent runs a deterministic fast path:

#. **observe** — gather telemetry evidence from the injected source backends
   (the same Sentry/Grafana backends the feature council uses), degrading
   gracefully when a backend is disabled or fails.
#. **detect** — turn evidence above a confidence threshold into incidents
   (:func:`dsf.sre.detect.detect_incidents`).
#. **fix_forward** — route each incident to its product repository and file an
   issue carrying the SP4 :data:`~dsf.contracts.handoff.HANDOFF_LABEL` so the
   coding squad triages it through the same intake as council issues. Repeated
   incidents are deduplicated by fingerprint.
#. **reflect** — record the action and a product-scoped lesson for the learning
   loop.

The slow path (feeding signals back to the feature council) is intentionally
out of scope here — see ADR 0008.
"""

from __future__ import annotations

import logging
from typing import TYPE_CHECKING

from dsf.config.registry import load_registry, route_product
from dsf.contracts.handoff import HANDOFF_LABEL
from dsf.sre.detect import detect_incidents
from dsf.sre.models import SRE_LABEL, Incident, SreSweepResult

if TYPE_CHECKING:
    from dsf.config.registry import Product
    from dsf.contracts.models import EvidenceItem
    from dsf.ports import ConfigStore, GitHubClient, MemoryStore, SourceBackend

_logger = logging.getLogger(__name__)

#: Working-tier key prefix used to deduplicate already-filed incidents.
_FP_PREFIX = "sre:fp:"


class SreAgent:
    """Observe production and fix-forward incidents to the coding squad."""

    def __init__(
        self,
        github: GitHubClient,
        memory: MemoryStore,
        config: ConfigStore,
        backends: list[SourceBackend],
        *,
        registry: dict[str, Product] | None = None,
        threshold: float = 0.7,
    ) -> None:
        self.github = github
        self.memory = memory
        self.config = config
        self.backends = list(backends)
        self._registry = registry
        self.threshold = threshold

    async def observe(self, scope: dict) -> list[EvidenceItem]:
        """Gather evidence from every backend, skipping any that fail."""
        evidence: list[EvidenceItem] = []
        for backend in self.backends:
            try:
                items = await backend.gather(dict(scope))
            except Exception:  # noqa: BLE001 - degrade, never propagate
                _logger.error("SRE observe backend failed", exc_info=True)
                continue
            evidence.extend(items)
        return evidence

    def _repo_for(self, product: str) -> str | None:
        """Route a product hint to its GitHub repository, or ``None``."""
        if self._registry is None:
            self._registry = load_registry()
        matched = route_product([product], self._registry)
        return matched.github_repo if matched else None

    async def fix_forward(self, incident: Incident, *, dry_run: bool) -> str | None:
        """File an issue for ``incident`` unless it is a duplicate or unroutable.

        Returns the filed issue URL, or ``None`` when nothing was filed (a
        duplicate, an unroutable product, or a dry run). A dry run deliberately
        does **not** index the fingerprint, so a later real sweep still files.
        """
        fp_key = f"{_FP_PREFIX}{incident.fingerprint}"
        if await self.memory.get_working(fp_key):
            return None
        repo = self._repo_for(incident.product)
        if repo is None:
            _logger.warning("SRE cannot route product %r — skipping", incident.product)
            return None
        if dry_run:
            return None
        labels = [incident.severity, SRE_LABEL, HANDOFF_LABEL]
        url = await self.github.create_issue(
            repo, incident.title, self._body(incident), labels
        )
        await self.memory.put_working(fp_key, True)
        return url

    async def reflect(
        self, incident: Incident, *, action: str, url: str | None = None
    ) -> None:
        """Record the incident outcome and a product-scoped lesson."""
        await self.memory.put_record(
            {
                "kind": "sre_incident",
                "product": incident.product,
                "fingerprint": incident.fingerprint,
                "severity": incident.severity,
                "action": action,
                "url": url,
                "text": incident.summary,
            }
        )
        await self.memory.put_lesson(
            {
                "product": incident.product,
                "kind": "sre_incident",
                "signal": f"sre:{action}",
                "severity": incident.severity,
                "text": incident.summary,
            }
        )

    async def sweep(self, scope: dict | None = None, *, dry_run: bool = False) -> SreSweepResult:
        """Run one full observe -> detect -> fix-forward -> reflect cycle."""
        scope = dict(scope or {})
        evidence = await self.observe(scope)
        incidents = detect_incidents(evidence, threshold=self.threshold)

        filed: list[str] = []
        duplicates = 0
        for incident in incidents:
            fp_key = f"{_FP_PREFIX}{incident.fingerprint}"
            if await self.memory.get_working(fp_key):
                duplicates += 1
                continue
            url = await self.fix_forward(incident, dry_run=dry_run)
            if url is not None:
                filed.append(url)
                await self.reflect(incident, action="filed", url=url)
            elif dry_run:
                await self.reflect(incident, action="dry_run")
            else:
                await self.reflect(incident, action="skipped")

        return SreSweepResult(
            observed=len(evidence),
            incidents=len(incidents),
            filed=filed,
            duplicates=duplicates,
            dry_run=dry_run,
        )

    @staticmethod
    def _body(incident: Incident) -> str:
        """Render the issue body for an incident."""
        lines = [incident.summary, ""]
        if incident.citations:
            lines.append("Citations:")
            lines.extend(f"- {c}" for c in incident.citations)
            lines.append("")
        sources = ", ".join(incident.source_kinds) or "telemetry"
        lines.append(f"Filed by the DSF SRE agent (sources: {sources}).")
        return "\n".join(lines)


__all__ = ["SreAgent"]
