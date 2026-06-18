"""S6 — Product Routing & Labeling (deterministic).

Map each accepted proposal to a product/repo via the Product Registry and shape
it into a :class:`RoutedIssue`: repo from ``product.github_repo``, labels drawn
from the product's label taxonomy by proposal kind/severity, and a body carrying
the problem, proposed change, and a grounded evidence appendix (each claim with
its raw citation).
"""

from __future__ import annotations

from typing import TYPE_CHECKING

from dsf.config.registry import Product, load_registry, route_product
from dsf.contracts.enums import ProposalKind, RunStatus
from dsf.contracts.handoff import HANDOFF_LABEL
from dsf.contracts.models import RoutedIssue
from dsf.observability.tracing import span_attrs_for_run
from dsf.orchestrator.blackboard import Blackboard

if TYPE_CHECKING:
    from dsf.container import Services
    from dsf.contracts.models import EvidenceItem, Proposal, Run

STATION = "S6:routing"

#: Proposal kind -> the taxonomy "type" label we prefer.
_KIND_TYPE = {ProposalKind.FIX: "bug", ProposalKind.FEATURE: "feature"}


def _severity_label(proposal: Proposal, taxonomy_severity: list[str]) -> str | None:
    """Pick a severity label from confidence, falling back to a middle tier."""
    if not taxonomy_severity:
        return None
    conf = proposal.confidence
    # Higher confidence in a FIX signal => higher severity.
    if conf >= 0.85:
        idx = len(taxonomy_severity) - 1
    elif conf >= 0.7:
        idx = min(2, len(taxonomy_severity) - 1)
    elif conf >= 0.5:
        idx = min(1, len(taxonomy_severity) - 1)
    else:
        idx = 0
    return taxonomy_severity[idx]


def _labels_for(proposal: Proposal, product: Product) -> list[str]:
    """Assemble labels from the product's taxonomy by kind/severity."""
    taxonomy = product.label_taxonomy
    labels: list[str] = []

    type_pref = _KIND_TYPE.get(proposal.kind)
    type_options = taxonomy.get("type", [])
    if type_pref and type_pref in type_options:
        labels.append(type_pref)
    elif type_options:
        labels.append(type_options[0])

    sev = _severity_label(proposal, taxonomy.get("severity", []))
    if sev:
        labels.append(sev)

    # The universal council->squad handoff signal: squad triage keys on this.
    labels.append(HANDOFF_LABEL)

    return labels


def _evidence_appendix(proposal: Proposal, run: Run) -> str:
    """Render a grounded evidence appendix listing each claim + raw citation."""
    by_id: dict[str, EvidenceItem] = {item.id: item for item in run.evidence}
    lines = ["", "## Grounded evidence", ""]
    for eid in proposal.evidence_ids:
        item = by_id.get(eid)
        if item is None:
            continue
        lines.append(f"- **{item.source_agent}**: {item.claim}")
        lines.append(f"  - citation: {item.raw_citation}")
        lines.append(
            f"  - provenance: query=`{item.provenance.query_used}` "
            f"source={item.provenance.source_kind.value} confidence={item.confidence:.2f}"
        )
    return "\n".join(lines)


def _issue_body(proposal: Proposal, run: Run) -> str:
    """Compose the issue body: problem + proposed change + evidence appendix."""
    parts = [
        "## Problem",
        "",
        proposal.problem,
        "",
        "## Proposed change",
        "",
        proposal.proposed_change,
        _evidence_appendix(proposal, run),
    ]
    return "\n".join(parts)


async def run(run: Run, services: Services) -> Run:
    """Route accepted proposals to products and build RoutedIssues."""
    with services.tracer.span("s6_routing", **span_attrs_for_run(run)):
        run.status = RunStatus.ROUTING

        blackboard = Blackboard(services.memory)
        proposals = await blackboard.load_proposals(run.id)
        registry = load_registry()

        issues: list[RoutedIssue] = []
        for proposal in proposals:
            hints = [h for h in [proposal.product, *run.scope_product_hints] if h]
            product = route_product(hints, registry)
            if product is None:
                run.audit.append(
                    _audit(
                        f"no product route for proposal {proposal.id} "
                        f"(hints={hints}) — skipped"
                    )
                )
                continue

            issue = RoutedIssue(
                proposal_id=proposal.id,
                product=product.key,
                repo=product.github_repo,
                title=proposal.title,
                body=_issue_body(proposal, run),
                labels=_labels_for(proposal, product),
            )
            issues.append(issue)
            run.audit.append(
                _audit(
                    f"routed proposal {proposal.id} -> {product.key} ({product.github_repo}) "
                    f"labels={issue.labels}"
                )
            )

        await blackboard.save_issues(run.id, issues)
        run.audit.append(_audit(f"routing: {len(issues)} routed issue(s)"))
        return run


def _audit(message: str):
    """Construct an audit record for this station."""
    from dsf.contracts.models import AuditRecord

    return AuditRecord(station=STATION, message=message)


__all__ = ["STATION", "run"]
