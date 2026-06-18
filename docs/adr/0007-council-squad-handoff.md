# ADR 0007: Council→Squad handoff — one system-level handoff label, label provisioning, wired triage

- Status: Accepted
- Date: 2026-06-18
- Fulfils: charter §6 SP4; builds on ADR 0001 (ports), ADR 0004 (ACA runtime), ADR 0006 (azure adapters). Supersedes nothing.

## Context

The feature council routes accepted proposals to a product repo and files GitHub
issues (S6 → S7); the provisioner initializes the coding squad (`squad init`,
`squad copilot`). But the handoff between them was not actually closed:

1. Nothing created the product repo's labels, so a real `gh issue create --label`
   would fail on missing labels.
2. There was no universal signal telling `squad triage` which issues are council
   output to pick up — the council's labels and the squad's triage were unaligned.
3. `squad triage` was never wired into provisioning.

## Decision

- **One system-level handoff label.** `dsf.contracts.handoff.HANDOFF_LABEL`
  (`squad:ready`) is stamped by S6 on *every* routed issue and is the single key
  `squad triage` filters on. It is a constant, not per-product taxonomy data, so
  the contract cannot drift per product and S6 stays independent of each product's
  label taxonomy. `contracts/` is the neutral layer shared by the runtime (S6) and
  the instance tooling (provisioner).
- **Label provisioning lives in the provisioner, not the filing hot path.** A new
  `create_labels` step (after `create_repo`, before `squad_init`) idempotently
  creates the product taxonomy labels + the handoff label via
  `gh label create --force --repo <repo>`. Creating labels per-issue at filing
  time would couple the hot path to label management and repeat work every run.
- **`squad triage --execute` is a wired provisioner step** (after `squad_copilot`),
  filtered to `--label squad:ready`, matching the charter mechanism. The squad owns
  its own scheduling; we wire and kick it rather than running a watcher here.
- **Multi-command steps.** `ProvisionStep` gains `commands: list[list[str]]` so one
  logical step (create_labels) can emit an ordered batch; `apply()` runs each under
  `--execute` and records them under dry-run.

## The closed knowledge loop

```
council S6/S7 → files issue (squad:ready) → squad triage --execute →
Copilot coding agent → PR → human review → council PR feedback-watcher →
record_outcome() → Lesson (MemoryStore.get_lessons) → next council run
```

The council-owned halves are tested offline end to end
(`tests/learning/test_handoff_loop.py`): every routed issue carries the handoff
label, and a merged squad PR for the product yields a retrievable lesson. The
squad's internal `.squad/` reflection is external and is covered by documentation,
not by an in-repo fake.

## Consequences

- A real instance's first filed issue lands cleanly (labels exist) and is
  immediately triageable by the squad.
- The handoff contract is one constant with one obvious place to change it.
- Live-GitHub / live-squad integration remains out of scope; the suite stays
  fully offline (the squad CLI runs through the injectable runner).
