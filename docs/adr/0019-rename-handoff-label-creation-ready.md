# ADR 0019: Rename the council->creation handoff label `squad:ready` -> `creation:ready`

- Status: Accepted
- Date: 2026-06-23
- Supersedes: —
- Relates to: ADR 0007 (council->creation handoff label), ADR 0016 (creation phase
  on the Copilot Coding Agent, supersedes ADR 0012), ADR 0009 (Azure SRE Agent intake)

## Context

ADR 0007 introduced a single system-level handoff label, `squad:ready`, stamped by
S6 on every routed issue. At the time the executor was the community **Coding Squad**
— a per-product Ralph `squad watch` loop (ADR 0012) that *polled* GitHub for that
label and scaled on its count, so the label name was also the trigger key.

ADR 0016 retired the Coding Squad: the **GitHub Copilot Coding Agent** is now the
executor, and S7 **assigns** it the issue directly (GraphQL) rather than having a
loop watch a label. ADR 0016 deliberately left the label value `squad:ready`
unchanged to keep the diff small ("only the executor behind it changes"). Every
other surface, however, moved to "creation" terminology: the maturity dial
(`squad_maturity` -> `creation_maturity`), the CLI flag (`--creation-maturity`),
and the phase name itself.

The result is a stale, self-contradicting contract: `dsf new` provisions a
`squad:ready` label for a "squad" that no longer exists, and the label's original
reason for its name (a `squad watch` loop keying on it) is gone. Operators
reasonably read it as a leftover.

## Decision

- **Rename the handoff label to `creation:ready`.** `HANDOFF_LABEL` in
  `core/src/dsf/contracts/handoff.py` becomes `creation:ready`; its description and
  module docstring are rewritten to describe the council->creation handoff and the
  Copilot Coding Agent executor (no `squad watch` loop). Color is unchanged.
- The label remains a **single system-level constant**, not per-product taxonomy
  (ADR 0007's rationale stands): S6 stamps it on every routed issue, S7 files under
  it and assigns the Coding Agent, and the SRE Agent reuses it for fix-forward
  issues. Renaming the constant updates all three call sites at once.

## Consequences

- `dsf new` now creates `creation:ready` (via `create_labels`); the SRE onboarding
  summary and operate runbook reference the new name.
- No migration: DSF is a blueprint that provisions fresh per-product repos, and the
  council files using the constant, so there is no stored `squad:ready` data to
  rewrite. Any pre-existing demo repo with the old label would need a one-time
  `gh label` rename, but no factory state depends on the literal.
- Historical ADRs/plans/specs that reference `squad:ready` are left as-is — they are
  immutable decision records of when the label was named that way. This ADR is the
  forward record of the rename.
- The broader concept docs still describing the retired Ralph/KEDA "Coding Squad"
  loop are outside this ADR's scope; only the label literal was updated there.