# Publish and adopt a reviewed charter

`dsf onboard decide charter` reviews the exact `.dsf/charter.md` before/after
and its complete evidence-linked analysis. Publication and adoption require
separate, current approvals. Neither activates a factory.

## Prerequisites and identity

Use the reserved, inactive factory, [complete source acquisition](acquire-source-inventory.md)
and [grounded inference](infer-product-charter.md). The installed CLI bundles the
matching runtime and existing provisioning assets. The deployed source image must
implement operator-only `POST /charter-review`; older images fail closed. Installing
the tool does not update an image, repair consent or recreate infrastructure.

The existing Azure CLI operator reads/writes private factory audit records.
The source host uses its existing code identity and read-only GitHub contents
token, plus the model host's current code-only `/authority` check. Review makes
no model calls or source/intent writes; it verifies the immutable analysis chain,
all available chunks, all limitations and whole source inventory independently
of display pages. No new role, store, identity, key or configuration is provisioned.

Only the operator's **effective `gh` identity** publishes the charter. No token
is exported, logged or stored. The factory's App/key never acquires content/PR-write
authority. Current durable repository ID, actual default branch, consent, operator
permissions, four allowed label meanings, visible workflow semantics and previously
reviewed external automation are rechecked before each relevant write. A known
delivery/agent trigger blocks the affected mutation; DSF never repairs it.

## Review and publish

Interactively run `dsf onboard decide charter --publish`. Missing factory locators
are prompted. The exact private review is displayed before asking you to type its
revision. Blank approval cancels; an old/different fingerprint is rejected. The
complete explicit equivalent command includes the approval. Review without
`--publish` is read-only.

Explicit review, including redirected use:

```bash
dsf onboard decide charter \
  --owner-appconfig-endpoint https://owner.azconfig.io --product widget \
  --tenant 11111111-1111-1111-1111-111111111111 \
  --subscription 22222222-2222-2222-2222-222222222222 --json
```

Inspect `review.before` and `review.after`: these are the complete exact file
contents, not selected hunks. `review.path` is always `.dsf/charter.md`.
`evidence` includes complete inference/check receipts, claims, original source
links, coverage and limitations. Treat this output as private. Use the returned
`revision` only after reviewing those inputs:

```bash
dsf onboard decide charter --publish --approve REVIEWED_REVISION \
  --owner-appconfig-endpoint https://owner.azconfig.io --product widget \
  --tenant 11111111-1111-1111-1111-111111111111 \
  --subscription 22222222-2222-2222-2222-222222222222 --json
```

Replace the illustrative locators and `REVIEWED_REVISION` explicitly; no environment
default supplies missing selections or approval. Redirected commands never prompt.
Every new PR needs its own current fingerprint.

Publication uses GitHub's native GraphQL mutations through `gh api`:
`createRef` targets the durable repository node ID; `createCommitOnBranch` targets
the returned native ref ID with the supported `expectedHeadOid` condition;
`createPullRequest` targets the same repository ID and reviewed branch names.
JSON goes through standard input, not shell interpolation. Reusable repository
names cannot redirect a mutation. The exact immutable commit/file diff and branch
are checked before creating the PR and again afterward.

Only a new proposal branch and its charter change are permitted. There are no
default-branch writes, force pushes, other documents, evidence sidecars, labels,
issues, assignments, approvals, merge, auto-merge or protection bypass operations.
Reviewers merge through normal GitHub governance outside DSF.

## Adopt human-merged or unchanged intent

After a human merges the original PR, review adoption:

```bash
dsf onboard decide charter --adopt \
  --owner-appconfig-endpoint https://owner.azconfig.io --product widget \
  --tenant 11111111-1111-1111-1111-111111111111 \
  --subscription 22222222-2222-2222-2222-222222222222 --json
```

This reads the exact original native PR identity, merge revision and human
`merged_by` observation. Pending, closed-but-unmerged, bot/auto-merged,
retargeted or uncertain PRs cannot govern. The merged file must equal the exact
current reviewed charter, and the merge must be on the actual default branch.
Run the same complete command with `--approve ADOPTION_REVISION` from this new
review to record adoption. Publication approval cannot approve adoption.

For a charter already present and unchanged in the complete analyzed source,
review with `--adopt --existing`, then repeat that complete invocation with
`--approve ADOPTION_REVISION`. No empty PR is created. This switch cannot bypass
the original publication receipt or human-merge requirement for a newly proposed
charter. Existing intent remains governing while an amendment is only proposed.

The explicit `grounded-onboarding-v1` Markdown profile supports unknown optional
non-goals/measures with reasons, but requires substantive purpose, intended users
and goals. Existing schema-1 charters remain supported under their unchanged
strict parser, including required success metrics. No unknown placeholder becomes
a goal or fabricated metric; no deterministic non-goal veto is introduced.
Human prose may span lines, and LF/CRLF documents are parsed without rewriting
them. Exact original content still determines review fingerprints, native blobs
and adoption receipts. Machine-inferred draft formatting remains strict.

## Current source reconciliation

A charter-only merge changes the source commit/tree. Review does **not** demand
another PR just because those IDs changed: it verifies the complete old inventory
and immutable analysis, compares canonical native Git trees across every changed
entry, and permits only the regular charter file and its containing directory to
differ. Equal immutable subtrees preserve all tracked hidden/ignored paths, not
just a selected page. The current charter bytes are reacquired and screened.
`evidence.source.reconciliation` distinguishes `ExactAnalyzedSource` from
`CompleteCharterOnly`; original analysis provenance is retained separately.

The comparison basis is the inventory and integrity commitment pinned by the
validated analysis, never a newer acquisition selected by another worker.
Historical inventory records, selection history and the complete integrity proof
are reverified when acquisition advances. Missing or changed original metadata
is `Unknown`, not permission to substitute the current inventory. A separately
acquired charter-only merge can still be reviewed and adopted without new analysis.
Before releasing fetched charter bytes, the source host checks their exact
snapshot again. Even another charter-only or same-tree commit observed during
that read blocks release and requires a fresh review; it cannot validate stale
bytes through historical reconciliation.

Substantive code/document differences, changed default branch, necessary unread
source or material charter edits block adoption. Acquire complete current source
with `source --new-snapshot`, run `infer --new-analysis` and its bounded advances,
then explicitly review compatible current intent again. A valid existing merge
can then be adopted without another empty PR. Human edits are never overwritten.

Missing/invalid/stale charters, revoked source access and uncertain integrity are
**current blockers**, even with a last-good adoption. Status labels retained
adoption separately as `historicalAdoption`. Analysis of source and unexecuted
tests is not a production observation: deployed-source correlation remains unknown.
Fresh model control-plane checks are not proof of inference availability or quota.

## Durable outcomes and limits

Read-only inspection:

```bash
dsf onboard decide charter --status-only \
  --owner-appconfig-endpoint https://owner.azconfig.io --product widget \
  --tenant 11111111-1111-1111-1111-111111111111 \
  --subscription 22222222-2222-2222-2222-222222222222 --json
```

`Reviewed`, `Published` and `Adopted` are distinct; `activated` is always false.
`Partial`, `Blocked`, `Failed`, `Canceled` and `Unknown` carry the blocked operation
and next responsible actor/action. Exit 0 means only the reported operation
succeeded; exit 1 means incomplete/blocked/failed/unknown, and 130 means canceled.

Private product App Configuration keys under `dsf/factory/charter/` retain stable
publication intents, approved payload chunks, per-effect intended/dispatched/
confirmed/known-failed/not-dispatched/unknown states, native request IDs and response hashes,
exact repository/ref/commit/PR identities, and adoption intents/receipts.
Publication history links prior operation IDs. Adopted records pin exact charter,
source, analysis chain, model revision, operator and time; payload/receipt loss
cannot become valid cached authority. No raw repository archive is retained.

Each key+label/value/content-type/metadata record is checked against 10 KiB before
effects. Each complete review payload is at most 512 records of 3000 serialized
characters; nothing is truncated. At most eight publication operations and eight
adoption revisions are admitted, including partial stages. Private charter audit
has a separate 8244-record/10-KiB-per-record ceiling, including at most three
immutable native-response records per publication; these records never supply
source authority or consume its existing 512-key governance allowance. Original
source and intent budgets are unchanged. Native source scans retain their existing bounded
time/node/page budgets; review host/client deadlines are 120/150 seconds.

There is no multi-key or GitHub/App Configuration transaction. Conditional ETags
coordinate DSF workers, not remote human actors. Native commit head comparison
does not make PR creation atomic with human branch changes. Unexpected changes
quarantine the result; cancellation is not rollback. An intended but undispatched
publication step can resume with its exact original inputs. Confirmed steps are
not repeated. A lost/ambiguous dispatch never recreates a branch or PR, searches
for ownership markers, adopts shared-App authorship, or force-overwrites work.
Inspect historical publication independently of current source/model readiness:

```bash
dsf onboard decide charter --inspect-publication \
  --owner-appconfig-endpoint https://owner.azconfig.io --product widget \
  --tenant 11111111-1111-1111-1111-111111111111 \
  --subscription 22222222-2222-2222-2222-222222222222 --json
```

Use its `publication.operation` and exact native `revision` with `--reconcile
--operation ORIGINAL_GUID --revision ORIGINAL_ETAG` and the same four locators.
This is an outcome-only conditional write, **not another GitHub request**. It can
resolve an original response retained by the acknowledged dispatcher before
closeout failed. The response is bound to factory, repository, immutable review,
operation and attempt. Each original successful response, at most 2048 UTF-8 bytes,
is retained losslessly as base64url in its own create-only record under the
publication's operation/attempt namespace. The original dispatcher then conditionally
pins that record's acknowledged native revision hash and exact content hash in the
publication coordinator before confirmation. No response bytes or reference can be
supplied by the reconciliation caller. A lost response-record acknowledgement
without that original coordinator pin remains `Unknown`, even if matching bytes
are discoverable. Audit automatically includes these records and validates pinned
references; existing inline response witnesses remain supported.

Before **each** new mutation or eligible retry, capacity checks reserve the entire
issued step's serialized closeout/reconciliation lifecycle, retaining prior attempts
and legacy witnesses, plus the complete response record and native metadata.
Capacity exhaustion returns `Partial` before another GitHub write; it does not
discard proof or roll back earlier confirmed effects.
Operator output withholds raw provider response and source
evidence, exposing original reviewed charter and native identities instead.
Reconciliation records the original revision, actor and time. A stale or racing
operator cannot overwrite a newer outcome.

No original response means `Unknown`, even if the branch, commit, PR, marker or
author looks correct. There is no uploaded-receipt or force option. Older
uncertain publications without a retained response cannot be retroactively
given creation provenance. `--reconcile` does not adopt a charter, restore current
readiness, clear pause, publish remaining steps or certify completed stop.

A native known rejection or original process's confirmed **no dispatch** can be
retried explicitly using `--retry --operation ORIGINAL_GUID --revision
ORIGINAL_ETAG --approve EXACT_CURRENT_REVIEW`, plus the four locators. This
requires fresh unchanged source/model/operator/mutation-automation checks and
open admission. It keeps the same branch/operation and bounded prior-attempt
receipts (at most eight total attempts); confirmed steps are never repeated.
Human branch edits block continuation. After positive reconciliation of an
intermediate step, a separately reviewed `--publish` may continue only the
remaining steps. Cancellation stops new dispatch, but an original response
arriving during cancellation may still be durably confirmed.

Council evidence/preview and activation remain separate. Adoption grants neither
standing GitHub write authority nor permission for live factory work.
