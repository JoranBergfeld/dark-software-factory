"""S7 — Issue Filing (deterministic).

Final dedup against prior issue titles, then either record the intended issue
(dry-run) or actually file it via the GitHub port. Sets the run status to FILED
and runs consolidation so the episode feeds the learning loop.
"""

from __future__ import annotations

from typing import TYPE_CHECKING

from dsf.contracts.enums import RunStatus, Verdict
from dsf.memory.consolidation import consolidate_run
from dsf.memory.dedup import FILED_ISSUE_KIND, dedup_key, is_duplicate
from dsf.observability.tracing import span_attrs_for_run
from dsf.orchestrator.blackboard import Blackboard

if TYPE_CHECKING:
    from dsf.container import Services
    from dsf.contracts.models import CouncilVerdict, Run

STATION = "S7:filing"

#: Memory record-kind for the filed-issue dedup index (shared with the
#: duplication critic via :data:`dsf.memory.dedup.FILED_ISSUE_KIND`).
ISSUE_KIND = FILED_ISSUE_KIND


async def run(run: Run, services: Services) -> Run:
    """File (or dry-run record) routed issues, then consolidate the run."""
    with services.tracer.span("s7_filing", **span_attrs_for_run(run)):
        blackboard = Blackboard(services.memory)
        issues = await blackboard.load_issues(run.id)
        verdicts = await blackboard.load_verdicts(run.id)

        dry = run.dry_run

        for issue in issues:
            key = dedup_key(issue.title, issue.problem)
            if await is_duplicate(key, services.memory, kind=ISSUE_KIND):
                run.audit.append(_audit(f"duplicate issue '{issue.title}' — not filing"))
                continue

            if dry:
                issue.filed_url = None
                run.audit.append(
                    _audit(f"DRY-RUN: would file issue '{issue.title}' to {issue.repo}")
                )
            else:
                url = await services.github.create_issue(
                    issue.repo, issue.title, issue.body, list(issue.labels)
                )
                issue.filed_url = url
                run.audit.append(_audit(f"filed issue '{issue.title}' to {issue.repo}: {url}"))

            # Index the title+problem key so future runs dedup against it.
            await services.memory.put_record(
                {"kind": ISSUE_KIND, "text": key, "repo": issue.repo, "run_id": run.id}
            )

        await blackboard.save_issues(run.id, issues)
        run.status = RunStatus.FILED

        await _consolidate(run, verdicts, services)
        run.audit.append(_audit(f"filing complete: {len(issues)} routed issue(s), dry_run={dry}"))
        return run


async def _consolidate(
    run: Run,
    verdicts: list[CouncilVerdict],
    services: Services,
) -> None:
    """Consolidate the run for learning — one Lesson per accepted verdict."""
    accepted = [v for v in verdicts if v.verdict == Verdict.ACCEPT]
    for verdict in accepted:
        await consolidate_run(run, verdict, services.memory)


def _audit(message: str):
    """Construct an audit record for this station."""
    from dsf.contracts.models import AuditRecord

    return AuditRecord(station=STATION, message=message)


__all__ = ["STATION", "ISSUE_KIND", "run"]
