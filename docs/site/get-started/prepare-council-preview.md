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

`/evaluate` conditionally records the original operation and its single eligible
worker in the approved native Cosmos item **before** returning HTTP `202`.
The CLI then polls read-only `/evaluation` every five seconds. Each HTTP hop is
bounded to 180 seconds, below Container Apps' 240-second ingress timeout; the
accepted hosted worker, not an open HTTP request, owns the longer evaluation.
Closing the terminal or losing that acceptance response does not cancel work.
Inspect the original operation rather than submitting another identity.

The runtime acquires current adopted intent, supporting complete analysis and
unknowns, exact application/environment scope, genuine fresh application
records, every selected supplemental source, and verified native model routes.
Once configuration is protected, unchanged original preparation proofs remain
reviewed provenance rather than expiring the configured operation mid-evaluation.
Operator-side checks still verify their retained hashes and all current native
receipts/material inputs; only the runtime's new source/model observations can
establish current readiness. This does not relax initial planning, approval or
publication freshness, or permit changed grants/configuration.
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
| `observedAt` | Time of this bounded observation; currentness is point-in-time, not a lease. |
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

While the original worker is active, inspection returns `Queued` or `Running`
without starting another evaluation. Completed-state inspection may return
`Checking` while a bounded read-only reader performs the complete current
checks; the CLI waits for that observation. The reader has one bounded slot,
no persistent effects, and can be repeated after a reader-process restart.
It is not a second evaluation queue. HTTP observations bind protocol `2`,
factory, original operation/approval and the caller's observation request ID.
Use a new request ID for a new observation; polling retains that ID.

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
Only a definitely stopped worker can conditionally release exact confirmed
state. A restarted process cannot take over an unconfirmed `Queued`/`Running`
worker, even before its first model call. Lost native admission/start receipts
and host shutdown remain auditable, nonreplayable uncertainty. Confirmed model
results and exactly reconciled native checkpoints remain reusable after a
confirmed release, never by assuming a lease has fenced the old worker.

The CLI permits 9,000 seconds for chained current checks and the runtime's
5,400-second execution budget. These are **operation**, not HTTP-ingress,
budgets. Execution policy `supervised-conveyor-v3` requires the reviewed image
with durable admission; an old synchronous image/configuration is not silently
upgraded or reused. Runtime execution has at most 62 native model
dispatches and a 120-second juror budget. Responses are at most 4 MiB.
Individual model/source hosts retain their existing 180-second hop bounds.
The complete input/proof reader retains its 1,800-second operation budget.
The approved input/execution policies pin additional evidence, context and
native-document limits. Overflow, cancellation or timeout is incomplete or
unknown, never silently truncated or successful.

This command persists bounded preview/provenance and readonly intended
publication. [Retention/export](retain-audit-evidence.md) is a separate
operator command. Final activation/live publication and uncertain-effect
recovery remain unavailable; the following command retains approval only.

## Review operator activation authority

This release supports **approval only**, not active onboarding or live filing.
The operator must have the original governance authority, current predecessor
access, private source-progress read access and native Cosmos point-read access
to `council-preview` and `runs`. It does not grant permissions, borrow a runtime
identity, redeploy hosts or use operator credentials to file issues.

Choose one nonempty activation operation GUID, distinct from the preview
operation. Obtain a complete read-only review:

```bash
dsf onboard decide activate --operation ACTIVATION_OPERATION_GUID \
  --owner-appconfig-endpoint https://OWNER.azconfig.io \
  --product PRODUCT --tenant TENANT_GUID --subscription SUBSCRIPTION_GUID --json
```

Review `review`, `approvalFingerprint` and the entire `evaluation`: charter,
boundary, analysis/unknowns, source/query/freshness, models/judgment, complete
native council result and intended proposal body. `evaluation.run` is the
hash-verified native stored run, not an independently rendered runtime copy.
The fingerprint pins native authority revisions, factory/repository, original
preview/run identities and hashes, exact protected references, operation and
effective Azure actor. Observation nonces, observation times and new telemetry
rows do not silently replace the original evaluated inputs.

All fully evaluated GO, SPLIT and NO-GO results can authorize approval.
Incomplete, failed, unreadable or stale evaluations cannot. Only unanimous GO
could qualify for later proposal publication.

Repeat the exact command with `--approve APPROVAL_FINGERPRINT`. Approval:

1. Rechecks current readiness through authenticated, read-only `/evaluation`,
   existing native preparation checks and the complete retained audit graph.
2. Protects the exact analysis before adding references.
3. Creates `dsf/factory/audit/activation/current` with `If-None-Match: *`,
   followed by immutable `{operation:N}/review` and `/approval` records.
4. Rechecks current readiness and pause around writes, then conditionally
   transitions the original coordinator with its native ETag.
5. Rechecks native history and current authority before reporting any
   currently valid approval.

These are separate native conditional writes, **not** a cross-key or
cross-store transaction. Approval receipts bind actor, approval time and the
new readiness observation time. Runtime analysis cannot write these records.
Duplicate contenders cannot overwrite the winner. Changed authority, stale
fingerprints, deliberate pause and conflicting/native-unknown transitions
withhold current approval. Lost acknowledgments, cancellation and missing
history never authorize replay, takeover, reset or rollback.

In this inactive profile the final result is `ApprovalOnly`, exit **1**:
`approvalRetained` and `approvalCurrent` may be true, but `isActivated`,
`historicallyCompleted` and `firstConfirmedFiling` remain false.
`promotionBlockedBy` names the missing live onboarding workflow and create-once
proposal-publication capability. No active manifest, new grant, issue or
repository mutation is produced. A separately reviewed supported live rollout
and actual promotion must precede activation; retained approval cannot enable
a future capability silently.

Read retained approval without writes:

```bash
dsf onboard decide activate --status \
  --owner-appconfig-endpoint https://OWNER.azconfig.io \
  --product PRODUCT --tenant TENANT_GUID --subscription SUBSCRIPTION_GUID --json
```

Successful review and valid `--status` inspection exit `0`. `Blocked`, `Failed`,
`Partial` and `Unknown` exit `1`; known pre-dispatch cancellation exits `130`.
Transient unreadiness withholds `currentReady`/`approvalCurrent` without
erasing the retained receipt. Unchanged recovery can restore current readiness,
not activation. Deliberate pause cannot be cleared by any activation request.
Interrupted admission remains inspectable and is never automatically replayed.
The original operation cannot be replaced by choosing another GUID.

All four locators and an operation are explicit for review/approval.
`--status` is mutually exclusive with `--operation` and `--approve`.
Redirected input and `--json` never prompt. Interactive use may prompt for
missing locators/operation, but approval is never inferred or prompted.
Review/status use current read/query checks, not new model inference.
Readiness remains point-in-time, not a lease or remote fence.

## Separately review the native Decide host

An `ApprovalOnly` receipt never authorizes a new runtime image implicitly.
`live` reviews a distinct native rollout, pinned to that exact original
operator approval and current complete readiness. It does not alter the
original preview, approval, charter, manifest or profile.

Provide a complete JSON selection with `operationId`, digest-pinned
`runtimeImage`, `audienceClientId` and `metadataRoleDefinitionId`.
The existing API registration/consent, private routing and exact COUNCIL
metadata role remain administrator prerequisites.

```bash
dsf onboard decide live --selection "$(cat live-selection.json)" \
  --owner-appconfig-endpoint https://OWNER.azconfig.io \
  --product PRODUCT --tenant TENANT_GUID --subscription SUBSCRIPTION_GUID --json
```

Review the complete returned plan, then repeat with
`--approve EXACT_LIVE_ROLLOUT_FINGERPRINT`. The command creates only the new
`<factory-prefix>-decide` Container App and its native authentication
configuration, plus read-only metadata assignments at that exact host and
its exact deployment. The runtime rereads the original native creation
operations/correlation and scoped grants; copied receipts alone are insufficient.
It reuses the original private environment and COUNCIL managed identity:
no new identity, secret, SQL grant, state container, model or telemetry backend
permission, application change or target tag write.

The new pinned image must implement `serve-decide`. Its native identity,
image, command/environment, capacity, tenant, audiences and allowed callers
are verified against original ARM creation/incarnation receipts.
Protected `dsf/factory/audit/live/current`, immutable plan chunks and
per-effect intent/acknowledgment/receipts retain the separate operator review.
The audit graph links that rollout to the exact immutable activation approval
and its existing source/analysis/preview provenance.

`PreparedAwaitingPublication` means **native rollout only**:
activation, onboarding completion and repository publication remain false.
`live --status` verifies the original resources and grants without writes or
inference. Unknown/lost/canceled native effects retain their original
operation; this command never retries a deployment, adopts an existing name,
replaces a host or treats lease expiry as fencing.

### Exercise the new host explicitly

Use a new explicit invocation GUID and the retained **live rollout**
fingerprint. This intentionally runs models but cannot file:

```bash
dsf onboard decide live --exercise INVOCATION_GUID \
  --approve EXACT_LIVE_ROLLOUT_FINGERPRINT \
  --owner-appconfig-endpoint https://OWNER.azconfig.io \
  --product PRODUCT --tenant TENANT_GUID --subscription SUBSCRIPTION_GUID --json
```

The operator authenticates only to the native host. The host uses its original
managed identity for all source/model/telemetry/state access, never operator
credentials. Authenticated `POST /exercise` admits a separately scoped native
execution before HTTP 202; subsequent `POST /execution` requests observe the
original bounded worker. The shared actual conveyor gathers current evidence,
synthesizes, grounds, embeds, deliberates and runs all three native jury
families. The original reviewed preview remains immutable.

Use `--execution INVOCATION_GUID` instead of `--exercise` for inspection.
Lost admission/worker responses, incompatible resumed scope and issued
effects remain unknown without takeover. Pause/current-authority checks run
at admission, after conditional admission, before worker execution and before
model dispatch. Already-issued effects may drain or remain unknown; cancellation
does not undo them.

The result explicitly says `NativeNonpublishingExercise`, never active/live
filing. Even unanimous GO is only retained evaluation here. Ordinary
`run`, `sweep`, schedules, HTTP intake, resume and outcome paths remain
closed by shared inactive onboarding admission, including supplied live-filing
booleans. Final promotion still requires the separately reviewed create-once
publication capability and an explicit new promotion approval.

All locators and complete selection/invocation inputs are mandatory when
redirected or using `--json`; no prompts or inferred approval.
Rollout review, native status and successful exercise return exit 0.
Blocked/failed/unknown results return 1; none imply rollback or successful
activation. Do not rotate operation IDs to evade uncertain history.

### Prepare the isolated native publication capability

After the original approved native live rollout, explicitly review a **new**
publication rollout. Existing `ApprovalOnly` and `PreparedAwaitingPublication`
receipts stay immutable and do not authorize publication.

The App/installation must have the [publication permissions before its initial
authority approval](verify-factory-github-authority.md). Missing pinned
permissions produce a named administrator prerequisite; DSF never changes the
App or rewrites old approvals to accommodate a permission delta.

Use a new operation GUID and a digest-pinned runtime image containing both
`serve-publisher` and `serve-publication-context`. The selection has the same
four explicit fields as the live rollout: `operationId`, `runtimeImage`,
`audienceClientId`, `metadataRoleDefinitionId`. The existing API registration
must permit the operator, original AUTH and original COUNCIL callers as required
by the reviewed routes. DSF creates no directory registration or consent.

```bash
dsf onboard decide live --publication \
  --selection "$(cat publication-selection.json)" \
  --owner-appconfig-endpoint https://OWNER.azconfig.io \
  --product PRODUCT --tenant TENANT_GUID --subscription SUBSCRIPTION_GUID --json
```

Inspect the complete returned native plan, then repeat with
`--approve <approvalFingerprint>`. Review writes nothing; redirected input and
`--json` never prompt or infer approval. `--publication --status` inspects the
original native operation and performs authenticated current provider checks,
without provisioning or replay.

The separate protected namespace is `dsf/factory/audit/publisher/`: its
conditional coordinator references chunked approved inputs, exact effects,
original HTTP 201 deployment acknowledgement, complete native Create operations,
resource incarnations and the authenticated native provider proof. Its audit
edges include the original operator approval, live rollout, GitHub authority,
plan chunks and creation receipts—not only the root record.

Native effects:

* A non-expiring `proposal-publications` container in the original factory
  Cosmos account/database.
* AUTH gets `readMetadata` and item read/create/replace **only at that
  container**. COUNCIL gets `readMetadata` and item read there, not write.
  Neither role grants query, delete, upsert, global keys or governance writes.
* New private, digest-pinned publisher and context hosts reuse the original
  AUTH and COUNCIL identities and original hosting environment. Original
  hosts, key version and CODE token are unchanged.
* The publisher's native Entra policy allows only the original COUNCIL
  principal/client. The context host admits AUTH and the original operator;
  route checks separate AUTH publication qualification from operator
  readiness inspection.
* Exact scoped read-only native metadata grants let these hosts verify their
  original resources, identities, role definitions, assignments and creation
  receipts. No application resource or backend tag is written.

After native provisioning, the operator calls the new COUNCIL context's
`/readiness`; COUNCIL calls the AUTH publisher's `/capability` with its own
managed identity. AUTH reads the original versioned key and verifies the actual
App, installation, restricted repository reach, effective token permissions,
proposal label and current mutation-class automation. Operator credentials are
never borrowed by either runtime workload. The authenticated proof is retained.
Missing/denied/unreadable provider evidence is not successful capability
preparation.

`PublicationPreparedAwaitingPromotion` means native publication infrastructure
is prepared; the result still reports `isActivated: false` and
`historicallyCompleted: false`. The first original native provider proof is
historical evidence; status and dispatch check current authority again.
Pause and exact protected revisions are checked before each native effect.
Lost or partial native creation stays unknown with its original operation;
there is no replay, adoption, replacement or completed-stop claim.

### Guarded create-once dispatch

The authenticated publisher's `/publish` route accepts only original invocation
and candidate identifiers—not payload, credentials, authority objects or a
live-filing boolean. It requires separate current protected final promotion,
the complete original capability and an exact new retained live-decision
scope. AUTH asks the original COUNCIL context to qualify the actual native run,
full model receipts, current source/model/telemetry inputs and unanimous GO.
Historical previews and nonpublishing exercises are incompatible.

Its stable native identity hashes the factory GUID, durable repository ID and
logical proposal intent, not the invocation GUID. The `proposal-publications`
container holds an immutable creation witness and a conditional journal with
the complete original payload/references before dispatch. Only one native
CAS contender can issue a create. Original HTTP 201 issue identity, request
ID and complete bounded response are retained; later candidate wording and
human issue edits never cause replacement. Losing a create response remains
unknown without search, ownership inference or another POST. A returned
401/403/404/422 rejection, or an acknowledged `NotDispatched` pre-create
failure, can retry the original payload after fresh checks.
Incomplete witness/journal history blocks recreation.

The issue token stays inside the GitHub authentication module. Its requested
repository-only permissions are `metadata:read`, `contents:read`,
`issues:write`, `actions:read` and `repository_hooks:read`; CODE's existing
contents token is unchanged. GitHub cannot confine `issues:write` to
create-only, and the App-key-holder residual remains. Current label semantics
and complete workflow/webhook inventories are checked before and after the
dispatch claim, using the attachment review's shared per-mutation policy.
Unrelated push, pull-request and check automation does not by itself block
issue-only creation. Applicable issue/opened or opaque dispatch consumers remain
blocked/unknown; an owner's declaration cannot clear uninspectable automation.
The current inventory hash and original explicit owner review are retained.
No issue PATCH, comment, closure, assignment, milestone/project change, outcome
relabel or delivery-trigger label is available.

Final promotion and the stable live-problem producer/all-entry routing are not
yet exposed in this release. Therefore `/publish` rejects the existing
approval-only factory, rather than treating native rollout as activation.
Automatic discovery/retention of publication-linked live execution also remains
to be wired. The native module and guarded route are installed, but no onboarding
completion or first confirmed filing is claimed until those remaining gates and
actual orchestration are completed.

## Deliberately pause council admission

Pause is separate from temporary unreadiness. It requires the original
operator's effective Azure CLI identity, owner registration access, and
product App Configuration data-plane read/write authority. It does not require
working models, telemetry, source analysis or a ready council.
No runtime identity receives governance-write permissions.

Read-only inspection:

```bash
dsf onboard decide pause \
  --owner-appconfig-endpoint https://OWNER.azconfig.io \
  --product PRODUCT --tenant TENANT_GUID --subscription SUBSCRIPTION_GUID --json
```

Explicit stop request, using one caller-chosen nonempty operation GUID:

```bash
dsf onboard decide pause --request --operation STOP_OPERATION_GUID \
  --owner-appconfig-endpoint https://OWNER.azconfig.io \
  --product PRODUCT --tenant TENANT_GUID --subscription SUBSCRIPTION_GUID --json
```

All four locators are required; redirected input and `--json` never prompt.
An interactive terminal may prompt for missing values, but cannot infer
`--request`. No activation approval is inferred from a stop request.

The first native `If-None-Match: *` create of unlabelled `dsf/factory/pause`
records the factory/repository, exact manifest revision and snapshot, profile,
actor, time and original stop identifier. It closes admission before the
separate protected audit copy. These writes are **not** a transaction.
Concurrent contenders cannot overwrite the winning request.

| Result | Meaning |
| --- | --- |
| `NotRequested` | No deliberate stop observed; not readiness, activation or permission to run. |
| `DrainingUnknown` | Original stop and audit copy confirmed; admission closed, but outstanding work/effects are not certified stopped. |
| `Partial` | Original stop closes admission; audit copy/readback unconfirmed. No automatic repair or replay. |
| `Blocked` / `Failed` | Input, authority or conditional write rejected; inspect any original request. |
| `Unknown` / `Canceled` | Stop effect or authority unconfirmed; inspect the same operation, never infer rollback. |

`admissionClosed: null` means the command could not confirm that fact;
`stopCompleted` and `isActivated` are always false. Exit `0` means successful
inspection or a confirmed stop request, **not** completed drainage. Other
results exit `1`, or `130` for reported cancellation.

The shared runtime gate denies manual runs, sweeps, scheduled work, HTTP
intake and incompatible resumed work. Supervised council hosts also check stop
authority before admission and model dispatch; a stop racing native admission
quarantines the contender before worker dispatch. A stop observed after an
already-issued model result preserves uncertainty, not a false no-dispatch
receipt. Existing issued effects may still finish. Cancellation, request
timeouts and lease expiry are not remote fencing.

No readiness refresh, `--resume`, live-filing boolean or repeated pause request
clears the deliberate stop. Missing coordinator with surviving stop history
fails closed. There is no unpause, takeover, deletion or forced-reset command.
Historical evidence remains in protected/native records and can be inspected
with `audit`; pause does not erase it or certify onboarding completion.

Use a reviewed runtime image containing these guards. The command does not
silently upgrade an older pinned image or certify its enforcement; older or
already-admitted workers remain part of the unresolved draining set.
Infrastructure, model/query costs already incurred, and retained audit storage
remain. This safety checkpoint still leaves onboarding live filing disabled.
