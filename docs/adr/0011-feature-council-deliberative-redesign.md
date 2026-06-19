# ADR 0011: Feature council deliberative council and validation jury

- Status: Accepted
- Date: 2026-06-19
- Fulfils: the feature-council decision-path redesign (design spec 2026-06-19). Builds on ADR 0001 (ports), ADR 0004 (ACA runtime), ADR 0007 (squad handoff, unchanged), ADR 0009 (SRE fast-path boundary). Supersedes nothing.
- Design detail: [`docs/superpowers/specs/2026-06-19-feature-council-deliberative-redesign-design.md`](../superpowers/specs/2026-06-19-feature-council-deliberative-redesign-design.md)

## Context

The council decides with deterministic critic functions. `council/decision.py`
runs each enabled critic and `CouncilVerdict.from_scores` applies one rule: any
critic veto kills the proposal, otherwise a weighted score must clear a
per-product threshold. Two problems follow once the factory matures and the
critics become model calls rather than fixed functions:

1. A single agent reaches the verdict. One critic's veto is enough to kill a
   proposal, with no deliberation and no diversity of perspective. The literature
   on LLM judging shows a lone judge is biased by self-enhancement and answer
   position, which is the failure this shape invites.
2. Intake can be pushed. A synchronous ingestion path sits next to the scheduled
   sweep, so an external source can drive the council's cadence. That is an
   ungoverned surface for a phase whose job is deliberate decision-making.

## Decision

- **Intake is a governed pull.** Sources collect into a buffer (Event Grid then
  Service Bus, per the charter). The council drains it on a schedule it owns. The
  synchronous push-into-the-pipeline path is removed from the council;
  event-driven urgency stays with the SRE incident fast-path (ADR 0009). Debounce
  is retained.
- **Facts stay deterministic, judgment gets deliberated.** Whether a claim traces
  to evidence (grounding) and whether an issue is a duplicate (dedup) are facts
  and stay hard gates. Whether a proposal is worth building is judgment and gets
  debated and juried.
- **A deliberation council replaces the parallel critic scoring.** Role-persona
  agents, one per decision lens (value, cost, feasibility, security, strategic
  fit), debate over one or two see-and-revise rounds with adversarial challenge,
  then a synthesizer produces one recommendation. This also absorbs the S3
  synthesis step.
- **A separate validation jury decides.** A smaller panel drawn from different
  model families, distinct from the deliberation tier, reads the recommendation
  and returns go or no-go with a consensus measure. Separating the proposer from
  the judge, and requiring model diversity, is where the bias reduction comes
  from.
- **A deterministic outcome policy maps the verdict to an action.** Strong
  consensus proceeds, a split escalates to a person, consensus against kills and
  logs. Given the jurors' verdicts the mapping is fixed, so it is unit-testable.
- **Maturity is one dial.** A per-product, runtime-adjustable maturity level sets
  two gates only: how much consensus is needed to act without a person, and
  whether the drafted spec auto-merges. The pipeline does not change as autonomy
  rises.
- **Spec authoring is a squad-boundary concern.** On proceed, the issue is filed
  with the `squad:ready` label (ADR 0007, unchanged) and a cloud agent drafts the
  spec as the Coding Squad's opening move. That boundary is specified with the
  squad, not here.

## Research grounding

The shape follows the multi-agent literature: debate-and-revise beats a single
reasoner on factuality (Du et al. 2023, arXiv:2305.14325); role-persona debate
works for evaluation, not only generation (Chan et al. 2023, arXiv:2308.07201);
layered propose-then-synthesize aggregation improves quality (Wang et al. 2024,
arXiv:2406.04692); a single LLM judge is biased by self-enhancement and position
(Zheng et al. 2023, arXiv:2306.05685); and a panel of smaller diverse judges
beats one large judge while its disagreement flags ambiguous cases for human
review (Verga et al. 2024, arXiv:2404.18796).

## Consequences

- New modules are expected for the deliberation council, the jury, and the
  outcome policy. The grounding and duplication critics are subsumed by the
  deterministic gates that already exist (S4 grounding, S7 dedup) and leave the
  debating set, which is the five lenses above.
- The per-product dials evolve rather than disappear: critic enable/disable
  becomes lens enable/disable, critic weights become debate influence, the accept
  threshold becomes the consensus bar, and new dials appear (maturity, debate
  rounds, jury composition). All stay per-product and adjustable while the line
  runs.
- The suite stays offline (ADR 0001, ADR 0005). Deliberation agents and jurors
  call the model through the injected port, so tests script their outputs and
  assert on pipeline behavior with no network; the outcome policy is unit-tested
  directly.
- **Plan 2 landed the deliberation council** (`council/deliberation.py`). The five
  lenses (value, cost, feasibility, security, strategic fit) each take a position,
  read their peers, and revise over `deliberation.rounds` rounds (a new per-product
  dial, default 2, floored at 1). `council/decision.py` runs the grounding and
  duplication gates as deterministic checks that can veto, then folds the gate and
  lens scores through the unchanged `CouncilVerdict.from_scores`. Three choices
  kept the change safe: grounding and duplication stay gates rather than becoming
  lenses, so the golden cases are untouched; offline each lens falls back to its
  former critic, so the synthesis is identical to the old critic loop and the eval
  gate stays green; and the synthesizer stays the deterministic weighted vote
  rather than an LLM verdict, so the decision stays auditable. The S3 evidence
  synthesis is left in place, since the grounding gate must run on proposals before
  the council; only the recommendation aggregation moves into this tier.
- **Plan 3 landed the governed pull intake.** A `SignalBuffer` port
  (`core/src/dsf/ports`) with an in-memory implementation
  (`core/src/dsf/signals/buffer.py`) sits between the sources and the council.
  `POST /ingest` (`triggers/app.py`) is now enqueue-only: it debounces, records,
  and enqueues, returning `{"status": "queued"}` rather than driving the conveyor,
  so an inbound source can no longer set the council's cadence. The scheduled
  worker runs `run_orchestrator_tick` (`triggers/scheduler.py`): `drain_signals`
  pulls the buffer and runs each signal through the conveyor in dry-run, then the
  source-kind sweep runs. A paused SIGNAL trigger leaves the buffer intact so
  queued signals wait rather than being dropped. The in-memory buffer is
  at-most-once; the real Azure Service Bus adapter (per the charter) will add
  lease/ack/dead-letter behind the same port, the same way the model/memory/config
  ports grew real Azure siblings later. `/file` stays the deliberate human filing
  path, unchanged.
- This ADR records the decision. The detailed design is in the spec, and the
  implementation was staged across follow-up plans. Plan 1 (model-diverse
  validation jury, per-product maturity dial, escalate outcome), Plan 2
  (deliberation council), and Plan 3 (governed pull intake) have all landed, so the
  whole ADR 0011 redesign is shipped. The phase doc marks the state honestly.
