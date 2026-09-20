# Review changes to retained factory inputs

`dsf onboard decide reconfigure` provides **read-only inspection and change
review** for a retained Decide factory. Applying reconfiguration is **not yet
available**. `Reviewed`, a matching fingerprint, and `canApply: false` do not
close live admission, change configuration, release claims, replace grants,
renew evidence, or approve activation.

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
provider ownership or cleanup authorization. In particular, an associated
hosting resource group is not a provenance-proven owned generation.

To reject stale replay, repeat the complete input with:

```text
--reviewed-fingerprint EXACT_CURRENT_REVIEW_FINGERPRINT
```

The fingerprint binds authoritative native revisions, desired inputs and
observed prerequisites. Changed claims, approval/evidence, configuration or
pause invalidate it. It is **not** application approval: there is no
`--approve`, `--apply`, force, rollback or generic-yes path.
Redirected input and `--json` never prompt; interactive terminals may prompt
for missing locators but still require an explicit inspection/input mode.

## Identity and operational limits

- Different durable repository ID, actual application environment,
  tenant/subscription or owner/product requires new onboarding, not identity
  retargeting. Rename/default-branch metadata alone preserves identity and
  does not require new activation approval.
- Changed App/installation permissions, installation scope, account or
  operator require eligible GitHub administrator review. No old permission
  record is edited to simulate consent.
- The review does not acquire claims, mutate tags, reconcile running work,
  read supplemental secret values, query telemetry, call models or evaluate
  a new council preview. Source access, effective grants, provider mutation
  safety, renewed analysis and full evaluation are not proven by this diff.
- Existing live admission is unchanged. Request [deliberate pause](prepare-council-preview.md)
  separately when needed; inspection preserves it and never claims issued
  work has drained.
- Original approvals, evidence and create-once publication records remain
  unchanged. Do not manually delete claims or restart provisioning to apply
  this plan. Safe staged application, retained claim generations, interrupted
  transition recovery and explicit renewed activation remain required
  before the full reconfiguration lifecycle can be used.
