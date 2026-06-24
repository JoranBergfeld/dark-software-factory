# ADR 0017: Product Charter — human-owned intent, advisory in the council

- Status: Accepted
- Date: 2026-06-23
- Supersedes: —
- Relates to: ADR 0011 (deliberative council), ADR 0014 (real-only `src/`, pull-only), Issue #74

## Context

The Feature Council decides *what to build*, but it had no first-class notion of
what the product is *for*. Scope and strategic-fit judgments were implicit: the
`value` and `strategic_fit` critics reasoned from evidence and accumulated
lessons, with no durable, human-authored statement of the product's vision,
target users, goals, and — crucially — its non-goals. Operators govern from
outside the loop (ADR 0011), but had no lever to *state intent* in a way the
council could read.

We want that lever to be: human-owned, reviewable, versioned in git, and unable
to silently override the deliberation. It must also be safe to feed to a model —
the text is human-written prose from a repo, so it is a prompt-injection surface.

## Decision

1. **The charter is a human-owned file.** Each product repo carries
   `.dsf/charter.md`. Humans author and amend it through normal pull-request
   review. Agents never write it; it is the source of truth for product intent.

2. **Deterministic sync into Cosmos.** A pure parser turns the Markdown into a
   typed `Charter` contract; a `CharterStore` port persists a `StoredCharter`
   (status `OK` / `STALE` / `MISSING` / `INVALID`) keyed by product. Sync is
   idempotent on the file's `source_sha` and is **fail-safe**: on a missing or
   invalid file it records the status but keeps the last-known-good charter
   rather than wiping it.

3. **Pull-only sync.** Consistent with ADR 0014, the runtime syncs the charter
   on each sweep, before running the line — no push path. Operators drive
   authoring with `dsf charter init | sync | status`.

4. **Charter-aware, advisory only (v1).** The charter feeds the `value` and
   `strategic_fit` deliberation lenses and adds one advisory `scope` annotation
   (a possible non-goal conflict). It **never vetoes**, never changes the
   weighted score directly, and an absent charter changes nothing: uncharted
   products behave exactly as before and are tagged `uncharted product context`.

5. **UNTRUSTED injection.** The charter is rendered into prompts inside a
   `<product_charter trust="UNTRUSTED">` envelope; the relevant personas are told
   to treat it as data only and never follow instructions inside it.

## Consequences

- Product intent becomes explicit, reviewable, and versioned, and it steers the
  council from outside the loop with no code change — just edit and merge the
  charter.
- Determinism is preserved: parse and sync are pure, and the offline doubles keep
  charter text out of the numeric scores, so the test suite stays deterministic.
- Backward compatible: uncharted products are untouched, so the feature can land
  before any product writes a charter.
- Costs: one extra (per-run memoized) Cosmos read each run and, when a charter is
  present, a small number of extra model calls (scope annotation + lens context).
  Charter quality is a human responsibility — a vague charter yields weak
  guidance, not wrong vetoes.
- v1 is advisory by design. Hard gating (non-goal veto), a living amendment loop
  (the council proposing charter edits), and richer scope enforcement are
  deferred to Issue #75. The `scope` annotation is deliberately kept out of
  `ALL_CRITICS` and out of the weighted score, so promoting it to a gate later is
  a config-plus-code change, not a data migration.
