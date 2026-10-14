# Review changes to retained factory inputs

`dsf onboard decide reconfigure` provides read-only inspection and exact
change review for a retained Decide factory. It can also conditionally admit
the reviewed transition. Admission first closes old work and durably retains
the original review and complete desired input. Later resumes report staged
configuration, claim, evidence-retirement, preview, and activation progress;
admission alone never claims that work stopped or any later stage completed.

The command reads the trusted owner reservation, product-native reviewed
plan, retained capability records and protected audit reference graph. A
local selection is desired input, never current authority. Missing or
inconsistent remote history is not reconstructed from that selection.
Complete retained telemetry, models, source and preview configuration are
required; incomplete preparation remains explicitly `Partial` or `Unknown`.

## Export current inputs

Use the original effective Azure and GitHub operators and explicit locator:

```bash
dsf onboard decide reconfigure \
  --owner-appconfig-endpoint https://OWNER.azconfig.io \
  --product PRODUCT --tenant TENANT_GUID --subscription SUBSCRIPTION_GUID \
  --inspect --json > current-review.json
```

`currentInputs` is the complete version-1 desired-input format. Copy that
object—not the entire response—to `desired-inputs.json`, then edit the
intended selections. For example, with `jq`:

```bash
jq '.currentInputs' current-review.json > desired-inputs.json
```

Keep these review files private. They contain configuration, nonsecret key
version references, resource identities and evidence identifiers, not tokens,
key contents, raw repository files or charter prose.

All input sections are required:

| Section | Reviewed inputs |
|---|---|
| `boundary` | Exact application membership, separate evidence backends/tables, dedication/exclusivity, prepared owner and repository automation declarations |
| `telemetry` | Image/audience, positive freshness seconds, query revision, exact per-table queries and stale queries |
| `source` | Complete CODE acquisition host image/audience; no sampling or execution permission |
| `models` | Synthesis, embeddings and three distinct-family jurors, exact deployments/accounts/roles and host selection |
| `supplemental` | Explicit FoundryIQ/WebIQ selection, query/disclosure, backend and version references; explicit `null` proposes removing all optional sources |
| `judgment` | Rounds, threshold and weighted lenses |

An omitted section never means “keep current” or “remove.” An empty
supplemental sources array is invalid; use explicit `null` for removal.
Telemetry accepts supported Analytics tables and their exact versioned
native queries, not arbitrary KQL. Freshness is 1–31,536,000 seconds.
Selecting a new image or audience does not create Entra consent, networking,
registry access or permission to replace an existing host.

## Review an exact diff

```bash
dsf onboard decide reconfigure \
  --owner-appconfig-endpoint https://OWNER.azconfig.io \
  --product PRODUCT --tenant TENANT_GUID --subscription SUBSCRIPTION_GUID \
  --input desired-inputs.json --json
```

Boundary changes require renewed human dedication and cross-owner
exclusivity declarations. First set `boundary.boundaryReview` to `null` and
inspect the returned `selectionFingerprint` and `findings`. After reviewing
the exact selection, set the existing review object's fingerprint to that
value, its accountable owner, and both explicitly accepted declarations.
Then repeat the command. A stale dedication review is `Blocked`; absence is
`Partial`. A declaration cannot override shared resources, unsupported
routes, conflicting tags, owner claims or missing authority.

The result distinguishes additions, removals, replacements and repository
metadata-only changes. It includes exact telemetry/model/supplemental grant
differences, preserved application assets, reused prerequisites, original
ownership/grant history, source/destination key-version custody, and affected
protected evidence references. Original historical receipts are not fresh

Apply support is deliberately narrower than read-only review. Any CODE
source, model topology/host, or supplemental selection change is `Blocked`
with `UnsupportedReconfigurationCategory` before admission closes. DSF never
reports those differences as applied or reconciles only part of their records
or grants. Original historical receipts are not fresh
provider ownership or cleanup authorization. In particular, an associated
hosting resource group is not a provenance-proven owned generation.

To reject stale review replay, repeat the complete input with:

```text
--reviewed-fingerprint EXACT_CURRENT_REVIEW_FINGERPRINT
```

The fingerprint binds authoritative native revisions, desired inputs and
observed prerequisites. Changed claims, approval/evidence, configuration or
pause invalidate it.
Redirected input and `--json` never prompt; interactive terminals may prompt
for missing locators but still require an explicit inspection/input mode.

## Admit and recover the transition

After reviewing the exact current result, choose a new operation GUID and
pass the review fingerprint as explicit application approval:

```bash
dsf onboard decide reconfigure \
  --owner-appconfig-endpoint https://OWNER.azconfig.io \
  --product PRODUCT --tenant TENANT_GUID --subscription SUBSCRIPTION_GUID \
  --input desired-inputs.json \
  --apply --operation OPERATION_GUID --approve EXACT_CURRENT_REVIEW_FINGERPRINT \
  --json
```

`--approve yes`, a stale fingerprint, incomplete input, or changed authority
is blocked before admission closes. The first conditional coordinator write
closes old council admission. The result is deliberately `Partial` and
`Quiescing`: admission closure is not a completed stop or configuration
application.

`Partial`, `Blocked`, `Failed`, `Canceled`, and `Unknown` use a nonzero CLI
exit. Automation must inspect serialized state and durable progress fields.

Inspect the retained operation without dispatch:

```bash
dsf onboard decide reconfigure \
  --owner-appconfig-endpoint https://OWNER.azconfig.io \
  --product PRODUCT --tenant TENANT_GUID --subscription SUBSCRIPTION_GUID \
  --status --operation OPERATION_GUID --json
```

If admission was interrupted while archiving input, recover only that
operation:

```bash
dsf onboard decide reconfigure \
  --owner-appconfig-endpoint https://OWNER.azconfig.io \
  --product PRODUCT --tenant TENANT_GUID --subscription SUBSCRIPTION_GUID \
  --input desired-inputs.json \
  --resume --operation OPERATION_GUID --json
```

Omit `--input` only when the retained archive is already complete. Repair
bytes must exactly match the admitted input hash and current review. A
missing coordinator is `Unknown`; a competing operation is `Blocked`.
Recovery never creates a replacement transition, reopens old work, rolls
back, or silently resumes the previous configuration.

The first successful resume reserves additions, journals exact before/after
configuration, applies each product record conditionally, then releases
removed owner claims only against their retained ETag and value hash.
Application tags are never written. A response loss leaves `Reconciling`;
repeat the same resume. Concurrent edits are `Blocked` or `Unknown`, never
overwritten or rolled back.

Capability commands are blocked during `Quiescing` and `Reconciling`. At
`AwaitingEvidence`, run only checks affected by the reviewed supported change.
Application/backend/query/telemetry changes require renewed telemetry proof;
judgment-only changes proceed to a new fully evaluated preview without
pretending unrelated capability evidence changed.
For example, a freshness/query/backend change uses:

```bash
dsf onboard decide telemetry \
  --owner-appconfig-endpoint https://OWNER.azconfig.io \
  --product PRODUCT --tenant TENANT_GUID --subscription SUBSCRIPTION_GUID \
  --verify-only --json
```

Resume again after current evidence is ready. DSF archives every old
preview, activation, live, publisher, and promotion record in chunked
transition history before conditionally retiring the exact native revision.
Interrupted retirement resumes by record index; a changed record is never
deleted.

Prepare, configure, seed, and fully evaluate a new preview through the
ordinary `preview-prepare`, `preview-configure`, `preview-seed`, and
`preview --evaluate` commands. These effects are admitted only while the
original transition is `AwaitingEvidence`. Resume advances to
`AwaitingActivation` only after the new durable preview is currently
`ReadyAwaitingActivation`.

Then perform a separate exact activation review/approval and final
`activate --promote` operation. Resume the reconfiguration once more.
Only a current active promotion and its completion receipt advances the
transition to `Completed`. Before that phase, runtime work and publication
remain closed. Completed transition records stay protected audit history but
no longer pause current-generation runtime admission.

Never delete transition records manually. Their input, configuration,
claim, retirement, evidence, and activation linkage is the recovery and
audit authority.

## Identity and operational limits

- Different durable repository ID, actual application environment,
  tenant/subscription or owner/product requires new onboarding, not identity
  retargeting. Rename/default-branch metadata alone preserves identity and
  does not require new activation approval.
- Changed App/installation permissions, installation scope, account or
  operator require eligible GitHub administrator review. No old permission
  record is edited to simulate consent.
- Read-only review does not acquire claims, mutate tags, reconcile running work,
  read supplemental secret values, query telemetry, call models or evaluate
  a new council preview. Source access, effective grants, provider mutation
  safety, renewed analysis and full evaluation are not proven by this diff.
- Inspection leaves live admission unchanged. Applying creates a separate
  durable admission-closing transition; it never claims issued work drained.
- App Configuration has no cross-key transaction. `snapshotRecords`,
  `appliedRecords`, `retiredRecords`, and `retiredApplied` are truthful
  durable progress, not an atomicity claim.
- Original approvals, evidence and create-once publication records remain
  audit history, not authority for changed governing inputs. Do not manually
  delete claims or restart provisioning.
