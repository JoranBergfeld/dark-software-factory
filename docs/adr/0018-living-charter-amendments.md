# ADR 0018: Living charter — factory-proposed amendments, human-gated

- Status: Accepted
- Date: 2026-06-23
- Supersedes: —
- Relates to: ADR 0017 (Product Charter), ADR 0014 (real-only `src/`, pull-only),
  ADR 0011 (deliberative council), Issue #75 (depends on #74)

## Context

ADR 0017 made the Product Charter a human-owned north star: a person authors and
amends `.dsf/charter.md` through pull-request review, and the runtime syncs the
merged file into Cosmos (pull-only). Products evolve, so the charter should be
revisitable *with evidence* — the factory accumulates lessons (rejected
proposals, SRE/PR outcomes) that may genuinely contradict or outgrow the stated
intent.

But letting an LLM propose changes to its *own governing intent* is a real
governance smell, even when a human merges. The risk is drift: the factory
nudging its own north star, or a prompt-injected lesson/charter steering the
proposal. We want the evidence-driven revision loop **without** granting the
factory any authority over the charter.

## Decision

1. **Propose, never apply.** The factory may open a pull request amending
   `.dsf/charter.md`; it has no merge authority and never writes the charter
   store. Applying a change still goes only through ADR 0017's merge→sync path,
   which reads `main` — never an unmerged branch. This keeps git the audit trail
   and humans the deciders.

2. **Deterministic guardrails gate the model.** `propose_charter_amendment`
   (`core/dsf/charter/amendment.py`) drafts via the model **only** after a chain
   of deterministic gates, each an auditable early return:
   - **opt-in** — `charter.amendment.enabled` (off by default, per-product
     override);
   - **baseline required** — only amends an existing, `OK` charter;
   - **evidence threshold** — at least `charter.amendment.min_lessons` lessons;
   - **one open PR per product** and a **cooldown**
     (`charter.amendment.cooldown_hours`), both derived from GitHub PR state via
     `latest_pr_with_head_prefix` (the `charter/amend/` branch prefix), not from
     mutable local memory.

3. **Evidence bundle + governance class.** The PR body carries the exact lessons
   that justify the change and an advisory banner; the PR is labeled `governance`
   + `charter-amendment` so branch protection / CODEOWNERS can require an
   anti-rubber-stamp review (a required reviewer who is **not** the proposer).
   The factory has no approval path, so proposer ≠ approver holds by construction.

4. **UNTRUSTED inputs, normalized output.** Both the current charter and the
   lessons are fed to the drafter inside `UNTRUSTED` envelopes with guard banners
   (reusing ADR 0017's charter envelope). The proposed charter's `product` key and
   `schema_version` are forced back to the baseline and source provenance is
   stripped, so a proposal can never retarget another product or forge a sync SHA.

5. **Runs on the sweep, after sync.** A `trigger:charter-amend` step runs after
   the charter sync and before the conveyor (`run_sweep`). Every guardrail and the
   final decision are recorded as a run-audit line; all errors are swallowed, so a
   reflection problem never tears down the sweep (same discipline as the sync).

## Consequences

- The north star becomes revisitable with evidence while staying human-owned: the
  factory surfaces a reviewable proposal, a person decides. Nothing changes for a
  product until `charter.amendment.enabled` is turned on.
- Safety is structural, not probabilistic: the gates are deterministic and the
  store is never written by this path, so the worst case is a low-value PR a human
  closes — never silent drift. The one-open-PR and cooldown gates read GitHub
  state, so they survive restarts and remain correct without extra persistence.
- Determinism in tests is preserved: the model only writes prose inside the gates,
  and the guardrails are exercised with deterministic doubles
  (`RecordingRepoClient` seeded PRs, `InMemoryMemoryStore` lessons).
- Costs: when enabled, one extra `pulls` list call per sweep and, past the gates,
  one drafting model call. Amendment quality is a human responsibility — a weak
  proposal is reviewed and closed, not merged.
- Deferred: a **deterministic non-goal veto** (structured non-goal IDs +
  machine-checkable scope rules) is a natural sibling but needs its own contract
  work; it is tracked separately, not bundled here.
