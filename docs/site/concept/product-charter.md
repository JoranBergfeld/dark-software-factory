# Product Charter

> State what the product is *for*. The charter is a human-owned file in the
> product repo that captures vision, target users, goals, non-goals, and success
> metrics. The Feature Council reads it to stay on-mission — as guidance, never as
> a gate.

## Why

A product never runs out of things it *could* do; the charter says what it
*should* do. It is the one place a human writes down intent so the council can
weigh proposals against it — and so anyone can later see what "on-mission" meant
when a call was made. It is owned by people, versioned in git, and changed only
through review.

## The file

Each product repo carries `.dsf/charter.md`. It is plain Markdown with a small,
fixed set of sections that parse into a typed charter:

- **Vision** — one or two sentences on what the product is for.
- **Target users** — who it serves.
- **Goals** — what it is trying to achieve.
- **Non-goals** — what it deliberately will not do (this is what the advisory
  scope check reads).
- **Success metrics** — how "working" is measured.
- **Constraints** and a **Glossary** are optional.

You never hand-edit the stored copy; you edit the file and merge it.

## Authoring and sync

- `dsf charter init --product <p>` runs a short interview and opens a pull request
  that adds `.dsf/charter.md`. A human reviews and merges it.
- On every sweep the runtime syncs the merged file into Cosmos deterministically.
  Sync is idempotent on the file's content hash and **fail-safe**: if the file is
  missing or doesn't parse, the council keeps the last good charter and records
  the status rather than dropping it.
- `dsf charter status --product <p>` prints the stored charter's status and any
  drift vs the file: `OK`, `STALE` (file changed, not yet re-synced), `MISSING`,
  or `INVALID`.

## How the council uses it (advisory)

The charter is **advisory in v1**. It changes *reasoning*, never the verdict
mechanics:

- The **value** and **strategic_fit** lenses get the charter as context, so
  deliberation can argue a proposal for or against stated goals.
- An advisory **scope** annotation flags a *possible non-goal conflict*. It is
  attached to the verdict rationale and the run audit — it does **not** veto and
  is **not** part of the weighted score.
- A product with **no charter** is untouched: its proposals are tagged
  `uncharted product context` and scored exactly as before.

## Treated as untrusted

The charter is human prose from a repo, so it is a prompt-injection surface. It
is always injected inside a `<product_charter trust="UNTRUSTED">` envelope and the
personas that read it are told to treat it as data and never follow instructions
inside it.

## Living charter (factory-proposed amendments)

A product's north star should be revisitable **with evidence** — but an agent
editing its own governing intent is a real risk, so the factory may only
**propose**, never apply. When enabled, the sweep runs a charter-reflection step:
if accumulated lessons warrant it, the factory drafts an amendment and opens a
**governance-labeled pull request** against `.dsf/charter.md`. A human reviews and
merges it; only the merge syncs into Cosmos (via the same pull-only sync above).
Nothing is ever applied from an unmerged branch.

Deterministic guardrails gate *whether* a proposal is even drafted — the model
only writes the prose inside them:

- **Opt-in** — off unless `charter.amendment.enabled` is set for the product.
- **Baseline required** — only amends an existing, valid (`OK`) charter.
- **Evidence threshold** — needs at least `charter.amendment.min_lessons` lessons,
  and the cited lessons are attached to the PR as an evidence bundle.
- **One open PR per product** and a **cooldown** (`charter.amendment.cooldown_hours`)
  between proposals, both derived from GitHub state.
- **Governance class** — PRs carry `governance` + `charter-amendment` labels so
  branch protection can require an anti-rubber-stamp review (proposer ≠ approver).

The lessons are fed to the drafter inside an `UNTRUSTED` envelope, just like the
charter, and the proposed charter's product key and schema are forced back to the
baseline so a proposal can never retarget another product.

## Out of scope (for now)

A **deterministic non-goal veto** (structured non-goal IDs + machine-checkable
scope rules) and charter-shaped synthesis remain future work.

