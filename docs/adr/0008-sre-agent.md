# ADR 0008: SRE agent — deterministic fast path that fix-forwards to the coding squad

- Status: Superseded by [ADR 0009](0009-leverage-azure-sre-agent.md)
- Date: 2026-06-18
- Fulfils: charter §6 SP5; builds on ADR 0001 (ports), ADR 0004 (ACA runtime), ADR 0005 (no `src/` fakes), ADR 0007 (council→squad handoff). Supersedes nothing.

> **Superseded (2026-06-18):** DSF now leverages the managed **Azure SRE Agent**
> product instead of the bespoke deterministic sweep described below. The
> `src/dsf/sre/` package and `dsfctl sre-sweep` CLI were removed; the provisioner
> renders a per-product onboarding runbook (`onboard_sre_agent` step) instead of
> deploying a `dsf-sre-<product>` Container App. The handoff contract is
> unchanged — the Azure SRE Agent files issues carrying `squad:ready` so the same
> `squad triage` intake picks them up. See [ADR 0009](0009-leverage-azure-sre-agent.md).

## Context

SP5 adds the SRE agent: the third corner of the factory triangle (council
proposes features, squad implements, SRE watches production). The brief is to
observe production telemetry, detect incidents, and route them somewhere they
get fixed — without duplicating the council's deliberation machinery or the
squad's implementation loop, and while staying fully offline-testable.

Two things already exist that the SRE agent should reuse rather than reinvent:

1. The **Sentry/Grafana source backends** (`dsf.agents.{sentry,grafana}.backend`)
   that gather `EvidenceItem`s — the same telemetry the council consumes.
2. The **SP4 handoff** (`HANDOFF_LABEL = "squad:ready"`) that the coding squad
   already triages on.

## Decision

- **Fast path only.** The SRE agent runs a deterministic
  `observe → detect → fix_forward → reflect` sweep (`dsf.sre`). It does *not*
  convene critics or score proposals; an incident above a confidence threshold
  becomes a GitHub issue routed to the product repo. The council "slow path"
  (feeding aggregated SRE signals back into deliberation) is intentionally
  deferred — see Consequences.
- **Reuse the source backends.** `observe` gathers from injected
  `SourceBackend`s (defaulting to the offline `SentryFixtureBackend` +
  `GrafanaFixtureBackend`), concatenating evidence and degrading past any
  backend that is disabled or raises — mirroring `SourceAgent.gather`. No new
  telemetry integration is introduced.
- **Reuse the SP4 handoff, mark provenance.** Fix-forwarded issues carry
  `HANDOFF_LABEL` so the *same* `squad triage --execute --label squad:ready`
  picks them up — one intake for both council and SRE. They additionally carry
  `SRE_LABEL = "sre"` and a severity label so the squad can tell SRE incidents
  apart and prioritize. No second handoff contract.
- **Deduplicate by fingerprint, in the working tier.** Each incident has a
  stable fingerprint (`sha1(product | normalized-claim)`). `fix_forward` indexes
  the fingerprint in `MemoryStore.put_working` *only when an issue is actually
  filed*, and skips incidents whose fingerprint is already indexed. A **dry run
  files nothing and indexes nothing**, so a later real sweep still files — the
  dry run is a true preview, never a suppressor.
- **Reflect for the learning loop.** Every handled incident writes a long-term
  record and a product-scoped lesson (`MemoryStore.put_lesson`), retrievable via
  `get_lessons(product)` — the same loop the council consolidation feeds.
- **Deploy symmetrically with the council.** The provisioner's `deploy_sre` step
  is un-deferred: `render_sre_bundle` renders `sre.containerapp.yaml`
  (`dsf-sre-<product>`) next to the council descriptor, and `--execute` runs
  `az containerapp update --name dsf-sre-<product>`, mirroring `deploy_council`.
  Rendering stays in `instance/` (provisioning tooling); the agent logic stays
  in `src/dsf/sre/` (runtime) — the same instance↔runtime boundary the rest of
  the codebase keeps.

## The fix-forward loop

```
prod telemetry → SRE observe (Sentry/Grafana backends) → detect (threshold,
severity, fingerprint) → fix_forward → files issue (sre + squad:ready) →
squad triage --execute → Copilot coding agent → PR → human review
                              ↘ reflect → Lesson (MemoryStore.get_lessons)
```

`dsfctl sre-sweep [--dry-run] [--product P]` runs one sweep in-process; the
provisioned `dsf-sre-<product>` Container App runs it on a schedule in azure mode.

## Consequences

- Production incidents reach the squad through the **same** intake as council
  issues, with no new label taxonomy and no per-issue label management.
- Repeated incidents don't spam the repo (fingerprint dedup), and dry-run
  previews are safe to run anytime.
- The council slow-path (SRE → council signals) and any live Sentry/Grafana
  wiring are out of scope here; the suite stays fully offline (fixture backends
  + the recording GitHub client). When the slow path lands it consumes the
  reflection records this agent already writes.
