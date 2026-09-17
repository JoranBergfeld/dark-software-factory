# Run a supervised council preview

`dsf onboard decide preview` reviews and provisions native preparation.
Its explicit `--configure` continuation publishes the protected capability,
conditionally creates the native execution seed through the real COUNCIL host,
and requires separate approval of that returned seed. `--evaluate` then runs
the actual Feature Council; `--evaluation` inspects persisted results without
writing. Neither
`ConfirmedNotReady` nor `ConfiguredAwaitingEvaluation` means readiness or
completed onboarding. No issue, PR, label, delivery or activation effect occurs.

First retain the reserved inactive foundation, verified GitHub authority,
complete source inventory/analysis, adopted charter, current model proof and
fresh application telemetry proof. Every selected supplemental source must
retain its original reviewed configuration, identity, grants and proof.
Missing, unreadable or orphaned selected history cannot become Azure-only.

## Explicit prerequisites

Use the same effective Azure CLI operator/tenant/subscription and `gh`
repository operator as the predecessor approvals. The command rechecks owner
registry authority, repository compatibility, adopted source/analysis
references, model routes, original identities, native resource incarnations
and current roles. It never repairs source, model, application or shared
configuration.

An Entra/identity administrator must provide the explicitly reviewed API
registration/client IDs and consent. A network/registry administrator must
provide existing private operator/workload reachability and access to the
pinned images. No API registration, consent, network relaxation, registry
secret or original-host upgrade is implicit.

An RBAC administrator must first provide existing custom metadata role
definitions with exactly the actions in the
[reviewed deployment contract](../concept/feature-council.md#reviewed-deployment-contract).
The command reads and fingerprints their native definitions; it does **not**
create or broaden those definitions. It creates only the exact scoped role
assignments in the approved plan. Missing role definitions or grantor rights
produce a blocked administrator handoff with the resource scope.

`plan.operatorPermissions` lists the required native deployment, host, Cosmos
container/SQL-role, and scoped role-assignment actions. Conditional or denied
permission is not treated as approval. COUNCIL receives no model-account,
telemetry-workspace, Search or secret backend grant through this command.
Existing source identities retain their own original backend permissions.

## Review and approve

An interactive terminal prompts for missing locators and the complete JSON
selection, prints the exact plan, then asks for its fingerprint. Blank
approval remains read-only. It also prints the equivalent explicit command.
Redirected input and `--json` never prompt.

Create `preview-selection.json`, replacing every placeholder with a reviewed
value. Choose one stable nonempty operation GUID; do not rotate it to escape
held history. All images must use immutable `@sha256:` digests.

```json
{
  "operationId": "<stable-operation-guid>",
  "runtimeImage": "<registry/runtime@sha256:digest>",
  "audienceClientId": "<council-and-context-api-client-guid>",
  "metadataRoleDefinitionId": "/subscriptions/<subscription-guid>/providers/microsoft.authorization/roledefinitions/<council-metadata-role-guid>",
  "models": {
    "runtimeImage": "<registry/runtime@sha256:digest>",
    "audienceClientId": "<model-consumer-api-client-guid>"
  },
  "telemetry": {
    "runtimeImage": "<registry/runtime@sha256:digest>",
    "audienceClientId": "<telemetry-consumer-api-client-guid>",
    "metadataRoleDefinitionId": "/subscriptions/<subscription-guid>/providers/microsoft.authorization/roledefinitions/<telemetry-metadata-role-guid>",
    "rowsPerTable": 1
  },
  "supplemental": [],
  "judgment": {
    "rounds": 2,
    "threshold": 0.6,
    "lenses": [
      { "name": "value", "weight": 1 },
      { "name": "cost", "weight": 1 },
      { "name": "feasibility", "weight": 1 },
      { "name": "security", "weight": 1 },
      { "name": "strategic-fit", "weight": 1 }
    ]
  }
}
```

`supplemental: []` is valid only with genuinely unselected history. For every
already selected source, supply one entry containing `kind` (`foundryiq` or
`webiq`), `runtimeImage`, `audienceClientId` and
`metadataRoleDefinitionId`. Omitting a selected source is not an opt-out.
No optional host, setting, grant or backend call is planned without selection.

The selection is at most 16 KiB. Telemetry sampling is explicitly 1-8 rows per
table using the versioned native content projection; the printed plan includes
the exact query, projection, source configuration and freshness revisions.
It does not silently rewrite the existing telemetry proof query or claim
exhaustive sampling. Judgment settings allow 1-2 rounds, a threshold in
`[0,1]`, and 1-5 distinct supported lenses with positive finite weights.
These are reviewed evaluation inputs, not evidence of evaluation.

Complete read-only plan:

```bash
dsf onboard decide preview \
  --owner-appconfig-endpoint https://OWNER.azconfig.io \
  --product PRODUCT --tenant TENANT_GUID --subscription SUBSCRIPTION_GUID \
  --preview-config "$(cat preview-selection.json)" --json
```

Inspect `plan.source`, `configuration`, `bindings`, `prerequisites`,
`operatorPermissions`, `steps`, and `costsAndPrerequisites`. The plan contains
the exact compiled ARM request, original creation bindings, proposed native
resource IDs, role principal/role/scope/actions and approval fingerprint.
It derives these from current predecessor authority, not operator-supplied
charter content, green proof claims or guessed resource names.

Repeat the complete command with:

```text
--approve EXACT_APPROVAL_FINGERPRINT
```

Approval is checked against a freshly reconstructed plan. Missing or invalid
explicit selection fails before provider access; mismatched approval causes
no preparation or native write.

## Native effects and retained status

Preparation executes the packaged `onboard-preview-workloads.json` through
the real ARM adapter: four new hosts/auth policies, zero to two explicitly
selected optional hosts, the product-partitioned `council-preview` container,
custom SQL role, and exactly two COUNCIL container assignments. Then it
creates the reviewed native ARM metadata assignments. Existing private
environment, identities, account, database, `runs`, original hosts and
application/shared resources are preserved.

Four to six always-on hosts each request 0.25 CPU/0.5 GiB. Their compute and
additional Cosmos state incur residual costs even while preparation is
blocked or incomplete. There is no automatic cleanup or rollback.

Read-only inspection needs only the four locators:

```bash
dsf onboard decide preview --status \
  --owner-appconfig-endpoint https://OWNER.azconfig.io \
  --product PRODUCT --tenant TENANT_GUID --subscription SUBSCRIPTION_GUID \
  --json
```

| State | Meaning and next actor |
| --- | --- |
| `Reviewed` | No writes; operator may approve the exact fingerprint. |
| `Partial` | Durable preparation exists, but native effects are incomplete; inspect original records. |
| `ConfirmedNotReady` | Exact native effects/receipts confirmed; use the separately approved `--configure` continuation. |
| `SeedAwaitingApproval` | Native seed returned; operator must approve its exact identity, ETag, checksum and execution configuration. |
| `ConfiguredAwaitingEvaluation` | Protected configuration and native seed verified; evaluation remains separate. |
| `Blocked` | Required current authority/configuration differs or is unavailable; prerequisite owner must reconcile it. |
| `KnownFailed` | Definitive rejection, distinct from uncertain acceptance; no automatic retry. |
| `Canceled` | Cancellation recorded; inspect whether any effect was dispatched. |
| `Unknown` | Lost acknowledgement, incomplete/corrupt/orphaned history, ambiguous native outcome or ownership change; retain resources and request IDs, never replay. |

Exit `0` means a successful review, confirmed preparation, returned seed awaiting
approval, or verified configuration awaiting evaluation;
failure states exit `1`, cancellation `130`. Preparation/configuration
`currentReady` is always false.
Status verifies serialized journal integrity and current native receipts
without any state, deployment, publication or activation write. It retains
historical preparation when current authority is no longer confirmed.

`--resume --approve EXACT_APPROVAL_FINGERPRINT` uses the original stored plan;
omit `--preview-config`. It may acquire only an explicitly released journal
whose remaining steps are known-undispatched. Confirmed preparation is
reverified without redeployment. Dispatched, unknown, rejected, corrupt,
erased or still-owned histories cannot be taken over or retried. Lease expiry
does not establish remote fencing.

The operation has a 3,600-second deadline and a separately bounded 30-second
accounting closeout. Native deployment polling is at most 120 ten-second
waits. Plans are limited to 512 KiB/128 immutable 4-KiB chunks, at most 64
effects, 4-MiB native responses, and the native 10-KiB App Configuration
record limit. Full lifecycle capacity is checked before the first write.
Overflow never truncates records or expands scope.

Source capability observations can expire while ARM is deploying. An unchanged
observation that was fresh at the protected original approval remains historical
evidence for that native preparation, not current readiness. Native resources,
roles, configuration, source/adoption and original record hashes are still
checked; expiry alone does not force a deployment replay or another approval.
Initial planning/approval, capability publication and evaluation still require
fresh observations.

## Configure the confirmed preparation

Use the original four locators; do **not** supply another `--preview-config`
or `--resume`. The continuation derives every binding, operation, configuration
and manifest pin from the original protected plan and actual confirmed native
receipts. It never redeploys or adopts a resource.

```bash
dsf onboard decide preview --configure \
  --owner-appconfig-endpoint https://OWNER.azconfig.io \
  --product PRODUCT --tenant TENANT_GUID --subscription SUBSCRIPTION_GUID \
  --json
```

Inspect `publication`: exact protected key/value writes, previous-value hashes,
COUNCIL endpoint/request identity, and `seedIntent` containing the database,
container, `/product` partition value, complete initial document and execution
policy. The initial document is only an intended request, **not** a native seed
receipt. Approve this review by repeating the command with
`--approve EXACT_PUBLICATION_FINGERPRINT`.

The command conditionally publishes the bindings/effects, capability and
manifest pins, then invokes `POST /prepare-evaluation` through the authenticated
COUNCIL host. The runtime verifies native authority and conditionally creates
the one intended Cosmos item; no context collection, synthesis or juror
evaluation is performed. Its actual `_rid`, quoted ETag and checksum appear
in `publication.execution.seed`.

The result is `SeedAwaitingApproval`, with a **different** approval fingerprint.
Inspect that complete execution configuration, then repeat `--configure` with
`--approve EXACT_RETURNED_SEED_CONFIGURATION_FINGERPRINT`. This conditionally
creates the execution configuration and updates the exact capability/manifest
pins. The real host rechecks the native seed without writing. Success is
`ConfiguredAwaitingEvaluation`, never Ready or activated.

Interactive use displays each review and prompts for that exact fingerprint;
blank approval remains read-only. Redirected/`--json` use never prompts or
assumes approval. All four locators remain required. Two stages need two
explicit approvals; the runtime has no protected-configuration writer and
cannot approve its own returned receipt.

Read-only configuration/seed inspection:

```bash
dsf onboard decide preview --configure --status \
  --owner-appconfig-endpoint https://OWNER.azconfig.io \
  --product PRODUCT --tenant TENANT_GUID --subscription SUBSCRIPTION_GUID \
  --json
```

Status uses `POST /preparation`, a read-only authenticated runtime route.
`publicationState` retains the typed coordinator and any returned native seed;
each stage's original explicit approval remains separately persisted.
It never calls the create route to infer absence or repair erased state.
Already confirmed configuration is reverified without republishing or
recreating the seed. A missing root with surviving history, missing journal
step, modified grant/incarnation, missing native item or lost acknowledgment
remains nonready. Unknown effects are quarantined; neither this continuation
nor `--resume` retries them.

The originally reviewed COUNCIL image must implement `/prepare-evaluation`
and `/preparation` with this protected publication protocol. Entra audience,
private network reachability and operator access remain explicit prerequisites.
An older pinned host image is not silently upgraded, nor does DSF repair
private routing. An administrator must reconcile an unsupported or uncertain
original preparation; selecting another operation ID is not recovery.

Protected publication uses the same 3,600-second outer deadline and 30-second
accounting closeout. `publication.policy` pins these repository defaults as
`protected-preview-publication-v1` in both explicit approval fingerprints.
Each stage allows at most 96 writes and a 512-KiB review;
the exact initial seed document is at most 16 KiB and the returned receipt at
most 4 KiB. Complete lifecycle records must fit native 10-KiB limits, including
the later execution pin. The protected namespace is bounded to 1,024 records
and 4 MiB, with no silent omission or truncation.

### Refresh predecessor proof

Existing `models --verify-only`, `telemetry --verify-only` and selected
`supplemental --verify-only` commands now recognize **only** exact metadata
additions with confirmed native receipts in the approved preparation. This also
works while other steps remain known-undispatched. They recheck the original
principal, role/actions, scope, condition, creator, native incarnation and
receipt revision. Unknown, unrelated, replaced or broader grants still fail.
No source credentials or backend grants move to COUNCIL.

Use these existing commands explicitly when proof expires, with the same
operator locators and originally reviewed inputs:

```bash
dsf onboard decide models --verify-only \
  --owner-appconfig-endpoint https://OWNER.azconfig.io \
  --product PRODUCT --tenant TENANT_GUID --subscription SUBSCRIPTION_GUID --json
```

Substitute `telemetry` or `supplemental` for the selected proof needing refresh.
These commands execute their actual proof paths and may incur model/query
costs. `--configure` does not silently run probes or repair evidence.
If a current model dependency stops native preparation, refresh its proof,
then use `preview --resume --approve ORIGINAL_PREPARATION_FINGERPRINT` with
the same four locators. Only released, known-undispatched work can continue;
confirmed resources and request identities are reused, never redeployed.
For already confirmed native preparation, refresh expired proofs before
`preview --configure`.

A compatible refreshed proof does not repin the preparation:
material model/source/configuration, charter/adoption, selected-source and
native resource changes still block. Original reviewed proof records remain
immutable and required; fresh checks append their own protected proof records
and update separate current-observation references. Erasing the original proof,
an unknown/dispatched native effect, missing history or receipt mismatch cannot
be repaired by proof refresh. Current checks are point-in-time, not
cross-provider atomicity. None of these steps asserts stale readiness.

## Evaluate intentionally

Final configuration output supplies `evaluationRequest.operationId` and
`evaluationRequest.approvalFingerprint`. Use these **capability** pins, not
either earlier publication-review fingerprint. Evaluation is an explicit
action; preparation and status never start it.

```bash
dsf onboard decide preview --evaluate \
  --owner-appconfig-endpoint https://OWNER.azconfig.io \
  --product PRODUCT --tenant TENANT_GUID --subscription SUBSCRIPTION_GUID \
  --operation CONFIGURED_OPERATION_GUID \
  --approve CONFIGURED_CAPABILITY_APPROVAL_FINGERPRINT --json
```

All six values are required for redirected/`--json` execution. Missing,
malformed or conflicting input causes no provider access or write. Interactive
use prompts for missing values; it does not infer approval. Effective Azure
and GitHub operator authority and exact current native preparation are checked
before contacting the protected COUNCIL `/evaluate` route.

The runtime acquires current adopted intent, supporting complete analysis and
unknowns, exact application/environment scope, genuine fresh application
records, every selected supplemental source, and verified native model routes.
It runs the existing conveyor in `OnboardingPreview`, with dry-run publication
and no issue filer or activation authority. Structured synthesis must actually
produce a candidate; semantic grounding must revisit original evidence and
counterevidence. Every enabled lens/round and all three distinct-family jurors
must complete. An empty selected optional source is valid; absent mandatory
application evidence is not.

Unanimous GO, either split, and unanimous NO-GO all qualify **only when fully
evaluated**. The result is `ReadyAwaitingActivation`, not activated or onboarding
complete. Early stop, empty/debounced input, malformed/unsupported claims,
missing jurors/rounds, unavailable models and partial/uncertain state cannot
produce readiness.

The output is complete bounded JSON in both terminal and redirected use:

| Field | Inspect |
| --- | --- |
| `readiness`, `reason`, `nextActor` | Current outcome and responsible next actor; never infer readiness from run status alone. |
| `evaluation.inputs.context` | Adopted charter, analysis/unknowns, application boundary, exact source/model/judgment revisions. |
| `evaluation.inputs.telemetry`, `.supplemental`, `.observations` | Original scoped records, freshness/query/source proofs, content hashes and nonnavigable WebIQ identifiers. |
| `evaluation.run.proposals[].grounding` | Individual supported claims, actual supporting quotes, counterevidence and residual uncertainty; citation existence alone is insufficient. |
| `evaluation.run.proposals[].councilReview` | Enabled lens rounds, typed recommendation and each of the three jurors' model identity, judgment and rationale. |
| `evaluation.run.proposals[].intendedPublication` | Exact proposed title/body, only `dsf:proposal`, no assignees; nothing was filed or handed off. |
| `evaluation.proof`, `.receipt`, `.dispatches` | Revision-bound durable proof, native run receipt and accounted native model observations. |

Evaluation and inspection exit `0` only for current
`ReadyAwaitingActivation`; otherwise `1`, or `130` for reported cancellation.
`Unknown` means an outcome or current authority is unconfirmed, not safe to
retry. Failed readiness leaves resources and accrued model/storage costs intact.

## Inspect or continue the original evaluation

Read-only inspection uses the same explicit locators and capability pins:

```bash
dsf onboard decide preview --evaluation \
  --owner-appconfig-endpoint https://OWNER.azconfig.io \
  --product PRODUCT --tenant TENANT_GUID --subscription SUBSCRIPTION_GUID \
  --operation CONFIGURED_OPERATION_GUID \
  --approve CONFIGURED_CAPABILITY_APPROVAL_FINGERPRINT --json
```

This invokes `/evaluation`; it never creates a seed, executes synthesis/jury,
writes checkpoints or publishes anything. Do not use preparation's
`--configure --status` to inspect an already-used seed.

The runtime validates the complete durable proof and current required
authority. Captured evidence becoming stale or material charter, analysis,
source, model, selection, configuration or native-grant drift withholds
current readiness. New telemetry rows do not invalidate still-fresh captured
records. Where current authority permits inspection, historical results remain
in `evaluation`, but only outer `currentReady: true` denotes current readiness.
If authority cannot establish access to the original operation, inspection
fails closed rather than reconstructing or replacing history. Final currentness
checks are point-in-time, not cross-provider atomicity.

An explicitly compatible continuation uses the original evaluation command
with `--resume`. It retains original captured context, operation/request IDs,
native checkpoints and confirmed model observations. Duplicate completed
evaluation rechecks its existing proof without inference or state writes.
Lost/canceled dispatch outcomes remain quarantined; a lease expiring is not
proof that a remote request stopped. No forced retry, alternate operation,
root reset, deletion, upsert or automatic recovery is available.

The CLI permits 9,000 seconds for chained current checks and the runtime's
5,400-second execution budget; no hidden 100-second default HTTP timeout
overrides that allowance. Runtime execution has at most 62 native model
dispatches and a 120-second juror budget. Responses are at most 4 MiB.
The approved input/execution policies pin additional evidence, context and
native-document limits. Overflow, cancellation or timeout is incomplete or
unknown, never silently truncated or successful.

This command persists bounded preview/provenance and readonly intended
publication. Full retention/export (#215), activation/live publication (#216)
and uncertain-effect recovery (#217) remain separate; no command here grants
their authority.
