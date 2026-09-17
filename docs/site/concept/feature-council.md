# Feature Council

> Decide what to build. The Council pulls a product's signals on a schedule,
> investigates each one against real evidence, deliberates the survivors through
> a council of role agents, validates the call with a separate model-diverse
> jury, and files the winners as labeled, de-duplicated GitHub issues.

## Why this phase

A product never runs out of things it could do. The hard part is deciding which
of those are worth building, and being able to show why. The Feature Council is
the part of the factory that makes that call. It turns raw signals (an error
spike, a recurring support theme, a competitor move) into a small set of
grounded, argued proposals, and it leaves an audit trail for every accept and
every kill. Because people sit outside the loop, the Council has to defend its
own decisions: every issue it files traces back to evidence, and every proposal
it drops is logged with a reason.

## Responsibilities

The Council runs as a seven-station line (the conveyor). Each station has one
job and hands the run to the next. These defaults describe greenfield operation;
the explicit onboarding evaluation profile below does not authorize delivery.

- **S1 Triage** normalizes a sweep into a scoped run and debounces duplicates.
  The orchestrator's in-process loop owns scheduled intake; there is no inbound
  signal queue in the .NET runtime.
- **S2 Investigation** collects structured evidence from every selected source.
  A missing or failed selected source fails investigation, rather than silently
  contributing nothing. An available source can honestly return no observations.
- **S3 Synthesis** clusters related evidence across source kinds into candidate
  proposals, retaining contributing evidence references and source kinds.
  It does not decide whether a proposal is worth building.
- **S4 Grounding** is a hard gate. It strips any claim that does not trace to a
  real evidence item, and kills any proposal left standing on nothing.
- **S5 Decision** deliberates, then validates. A council of role agents, one per
  decision lens (value, cost, feasibility, security, strategic fit), debates the
  proposal over one or two see-and-revise rounds and synthesizes a single
  recommendation. A separate, model-diverse jury reads that recommendation and
  returns go or no-go by consensus. A deterministic outcome policy turns the
  result into an action: strong consensus proceeds, a split escalates to a
  person, consensus against kills. Low creation maturity always requires human
  review. Malformed, missing, or failed juror results are errors, not approval.
  Every step goes to the log.
- **S6 Routing** maps each accepted proposal to a product and repo, attaches
  labels from the product's taxonomy, and writes the issue body with a grounded
  evidence appendix.
- **S7 Filing** does a final de-duplication against issues already filed, then
  files (or, in dry-run, records the intent), and consolidates the run into
  lessons for next time.

## The decision subprocess

S5 is where the Council earns its name. A grounded proposal is argued by a
council of role agents, validated by a separate panel, and a fixed policy turns
the result into an action. Two deterministic gates, grounding and duplication,
bracket the debate and can veto on their own; the five role lenses deliberate
over one or two see-and-revise rounds, and a deterministic synthesizer folds the
lens positions into one recommendation for the separate validation jury.

```mermaid
flowchart TD
    prop["Grounded proposal from S4"] --> gates
    prop --> lenses

    subgraph deliberate [Deliberation council]
        gates["Deterministic gates: grounding, duplication, each can veto"]
        lenses["Role agents: value, cost, feasibility, security, strategic fit"]
        lenses --> rounds["One or two see-and-revise rounds, adversarial challenge"]
        gates --> synth
        rounds --> synth["Synthesizer: weighted lens positions, disagreement, one recommendation"]
    end

    synth --> panel

    subgraph validate [Validation jury]
        panel["Smaller panel, different model families, separate tier"]
        panel --> verdict["Per-juror go or no-go, plus a consensus measure"]
    end

    verdict --> policy{"Outcome policy, deterministic"}
    policy -->|strong consensus| proceed(["Proceed: file issue with creation:ready"])
    policy -->|split| escalate(["Escalate to a person"])
    policy -->|consensus against| kill(["Kill, logged"])
```

The shape follows the multi-agent research. A council of agents that debate and
revise reaches better-grounded conclusions than a single reasoner (Du et al.
2023), and distinct role personas make debate work for judging, not only for
generating (Chan et al. 2023). The validation tier is kept separate and
model-diverse on purpose: a lone model judge is biased toward its own answers and
to answer position (Zheng et al. 2023), while a panel of smaller, diverse judges
tracks human ratings more closely and flags real disagreement for a person (Verga
et al. 2024). Splitting a propose step from a later synthesize step is the same
layered-aggregation result (Wang et al. 2024).

**Sources:**

- Du et al. 2023, *Improving Factuality and Reasoning in Language Models through Multiagent Debate*, [arXiv:2305.14325](https://arxiv.org/abs/2305.14325).
- Chan et al. 2023, *ChatEval: Towards Better LLM-based Evaluators through Multi-Agent Debate*, [arXiv:2308.07201](https://arxiv.org/abs/2308.07201).
- Wang et al. 2024, *Mixture-of-Agents Enhances Large Language Model Capabilities*, [arXiv:2406.04692](https://arxiv.org/abs/2406.04692).
- Zheng et al. 2023, *Judging LLM-as-a-Judge with MT-Bench and Chatbot Arena*, [arXiv:2306.05685](https://arxiv.org/abs/2306.05685).
- Verga et al. 2024, *Replacing Judges with Juries: Evaluating LLM Generations with a Panel of Diverse Models*, [arXiv:2404.18796](https://arxiv.org/abs/2404.18796).

## Inputs and outputs

**In:** evidence scoped to one product, gathered over A2A `/gather` from enabled
served source agents. The Microsoft-native roster is `azuremonitor`, `foundryiq`,
and `webiq`; unconfigured kinds remain disabled and receive no Container App.
The .NET runtime has neither an in-process S2 fallback nor an `/ingest` queue.
Event-driven urgency stays with the Operation phase.

**Out:** GitHub issues in the product's repo. Each one is labeled by type and
severity, carries the handoff label, and includes the problem, the proposed
change, and the evidence behind it with citations.

## Handoffs

Upstream, the Council consumes signals. Some come from outside the factory
(market and operational telemetry), and over time the Operation phase feeds production
lessons back in as new signals.

Downstream, the Council hands to the Creation phase. The contract is one label:
every filed issue carries `creation:ready`, and the GitHub Cloud Agent picks up issues carrying exactly
that label. The Council does not call the executor directly. It files an issue and
the label does the wiring, which keeps the two phases independent. See
[Handoffs](handoffs.md) for the full mechanism.

## Harness and steering

This is the most tunable phase, and most of the factory's dials live here:

- Pause/resume sweeps and adjust cadence through product App Configuration.
- Enable provisioned source agents through `agents.<kind>.enabled`.
- Set the per-product synthesis threshold.
- Configure lens enablement/weights, rounds, and the separate jury roster at
  deployment with `dsf new --decide-config`.
- Use `--dry-run` to run the line without filing anything.

Grounding and de-duplication are not optional dials. The grounding gate and the
final dedup always run, so the factory cannot file an ungrounded or repeated
issue regardless of how the other dials are set.

Sweep controls and source flags are read from App Configuration. Lens/jury
configuration and creation maturity are runtime environment settings, so changing
the deployed values requires a revision update. The synthesis threshold is not
a shortcut around the jury: only a valid unanimous-go result can proceed at
medium/high maturity, and low maturity always escalates.
Routing and filing reapply the current maturity even when S5 or S6 was already
checkpointed. Lowering maturity to low therefore escalates an earlier approval
instead of allowing it to auto-file.

## Where it lives and how autonomous it is today

The Feature Council is implemented in this repository under
`dotnet/src/Dsf.FeatureCouncil/` and hosted by `dotnet/src/Dsf.Runtime/`. It runs
end to end in dry-run through the real .NET runtime composition, and in
production as Azure Container Apps scoped to a single product (ADR 0004). It is
the most built-out phase of the loop: the full conveyor, the grounding gate, and
the filing path all run today.

S3 uses cross-source-capable lexical similarity clustering. S5 records typed lens
positions, deterministic synthesis, juror results, and an explicit
`Proceed` / `Escalate` / `Kill` / `Error` verdict. S6/S7 consume that verdict;
an escalated run retains its review package and cannot auto-file. Production
has no offline model fallback.
Persisted votes and recommendation fields must be explicit: an incomplete
review becomes an audited error, never an implicit go vote.

S3 resolves each cluster against persisted problem profiles using the same
lexical clustering policy. The resulting opaque problem ID, within the run's
scope namespace, identifies both filing intents and human lessons. References
and changing observation text are evidence, not the ID. Ambiguous matches to
multiple known problems fail for human consolidation rather than silently
merging their histories. Newly recognized observations extend the persisted
profile under the same ID, so subsequent runs can recognize them too.

The always-on orchestrator runs `serve-orchestrator --loop`, reads sweep controls
from App Configuration, and acquires a Cosmos lease before driving the line.
The `runs` and `learning` containers use the `/product` partition key; existing
legacy `/id` containers are retained separately.

Offline integration tests exercise HTTP hosting and station control flow with
scripted external boundaries. They are not evidence that a vendor API works,
that three different model families judged a real proposal, or that production
filed an issue. [Issue #183](https://github.com/JoranBergfeld/dark-software-factory/issues/183)
requires a separate live/staging manual sweep, an observed autonomous tick, and
the resulting GitHub issue. See [Operate it](../get-started/operate.md).

## Proposal-only onboarding evaluation interface

`ConveyorLine.RunAsync` also accepts the explicit `OnboardingPreview` profile,
with `DryRun = true` and a required `CouncilEvaluationContext`. This library
interface is not a runtime admission capability, a readiness proof, or a CLI
onboarding-completion command. The default `Greenfield` profile is unchanged.

The context carries the adopted charter, complete source analysis and unknowns,
selected application scope, original observation provenance, five reviewed model
routes, and intended lens/round/threshold configuration. The line pins its exact
hash as `EvaluationRevision` and rejects missing or changed inputs. Callers remain
responsible for current authenticated source, charter, telemetry, supplemental,
model and storage authority; structural context validation is not authorization.

The profile uses the existing `IFactoryModelWorkloads` interface, supplied through
`ConveyorServices.EvaluationModels`. S3 asynchronously embeds every supplied
observation, rejects omitted/duplicated/invented groups, and consumes structured
native synthesis into actual proposal title, body and cited claims. A model can
honestly produce no candidate. S4 asks the model to check semantic support and
counterevidence against original observations, retaining typed checks, verbatim
excerpts and limitations. Citation existence alone cannot pass that gate.

S5 runs every configured real lens and all three native-family jurors, retaining
completed rounds and individual results through the existing run-store seam.
All reasoning receives adopted intent and uncertainty. Unanimous GO, either
split, and unanimous NO-GO remain distinct complete evaluations; Creation
maturity does not override their proposal-only disposition.

Every fully evaluated proposal carries `IntendedPublication`: synthesized
title/body, supporting observations, semantic checks, uncertainty, deliberation,
individual jury results and deterministic disposition. Its only intended label
is `dsf:proposal`, with no assignee or delivery handoff. A GO reaches S6/S7 and
also records that shape in `PreviewedIssues`, without calling a filer. Split and
NO-GO stop after the saved S5 checkpoint and keep the shape on the proposal.
No path activates a factory or creates a GitHub issue.

Serialized run state retains the new context, model observations, synthesis,
grounding, review and publication fields. Model observations bind workload route,
configuration, actual response model and output hash; each call also records its
input hash and evaluation step. Prompts are bounded to 32,768 characters, native
text outputs to 8,192, supplied observations to 32, and rendered bodies to 60,000;
overflow fails explicitly rather than truncating evidence.

`Previewed` alone is not readiness: an empty run can reach that legacy terminal
status without any candidate. Early triage termination, unsupported claims,
incomplete judgments, provider failures and failed persistence must not qualify.
Provider exception details are withheld from onboarding audits and typed errors.
Continuation can retain durably completed rounds/jurors, but this interface does
not establish safe replay of an in-flight or unconfirmed external call. Runtime
composition must provide current authority, protected persistence, dispatch
accounting and a separately validated revision-bound readiness proof.

The runtime's Cosmos run-store adapter must be explicitly configured for
`OnboardingPreview`; its default remains `Greenfield`. Onboarding checkpoints use
native create-only inserts and `If-Match` replacements, never upsert or
read-then-write emulation. Their `/product` partition, run ID, protocol, complete
serialized evaluation checksum and native response ETag must match. All context,
claims, grounding, model observations, review and intended publication fields
survive a process restart. A missing profile cannot downgrade those records to
greenfield. Documents are limited to **2,080,768 UTF-8 bytes**, reserving 16 KiB
below Cosmos's 2 MiB item limit for native metadata.

Checkpoint storage requires an explicit workload credential, not a developer
credential-chain fallback. Denial, CAS conflict, preflight capacity and unknown
acknowledgment remain distinct failures. After an unconfirmed read/write the
same store instance refuses further writes for that run; this is not a remote
lease, revocation receipt or cross-provider transaction. Cancellation is not
rollback. Reconstructed artifacts and checksums establish neither current
readiness nor safe replay of external model dispatches.

The source workload exposes a shared implementation API,
`RuntimeVerbs.ReadSourceCharterContextAsync`, for reading adopted intent under
its own verified code-intent identity. It uses the actual source/inventory and
intent workflows, including the original analyzed inventory, complete analysis,
current native model authority and exact fetched-charter fence. A council
identity cannot be substituted for that source identity. The implementation
API itself grants no preview admission.

Its protected App Configuration reader reuses Core's
`FactoryCharterAdoptionReader` with the existing operator adoption path.
Adoption, coordinator, original intent, confirmed receipt and all bounded
payload chunks must agree, including the approving operator. Current pointer
revisions are checked around reading and native revalidation; missing/corrupt
history remains unknown. The result distinguishes retained adoption evidence
from current source context and performs no adoption, publication, activation
or source-progress writes. These sequential native checks are not a
cross-provider transaction or a complete council readiness assessment.

### Protected council source-context consumption

Two **runtime** commands provide connected, read-only source consumption;
explicitly selected hosts provide model evaluation, telemetry and supplemental evidence. They are not `dsf`
CLI verbs and are not yet created by provisioning assets.

| Runtime command | Host suffix | Workload identity | POST route | Native Entra caller |
| --- | --- | --- | --- | --- |
| `serve-preview` | `-preview` | Original COUNCIL UAMI only | `/source-context`, `/model-authority`, `/telemetry-evidence`, `/supplemental-evidence` | Factory's approved Azure operator only |
| `serve-preview` (selected supplemental callback) | `-preview` | Original COUNCIL UAMI only | `/supplemental-authority` | Requested selected source's original principal **and client** only |
| `serve-source-context` | `-context` | Original CODE UAMI only | `/council-context` | Original COUNCIL principal **and client** only |
| `serve-preview-models` (explicit selection) | `-preview-models` | Original COUNCIL UAMI only | `/evaluate` | Original COUNCIL principal **and client** only |
| `serve-preview-telemetry` (explicit selection) | `-preview-telemetry` | Original TELEMETRY UAMI only | `/evidence` | Original COUNCIL principal **and client** only |
| `serve-preview-supplemental` (selected sources only) | `-preview-foundryiq`, `-preview-webiq` | That source's original distinct UAMI only | `/evidence` | Original COUNCIL principal **and client** only |

Source/context/telemetry routes accept `PreviewCapabilityRequest`: `operationId`, `approvalFingerprint`,
and a nonempty `requestId`. `RuntimeCliApplication` runs the real hosts through
`IWebHostRunner`; `IFactoryPreviewCapability.CheckAsync` validates current
protected and native authority. The source route then calls
`RuntimeVerbs.ReadSourceCharterContextAsync` and the shared `RuntimeWorkflow`.
The council proxy acquires its own COUNCIL token for `api://{audience}/.default`;
it never forwards an operator token, borrows CODE credentials, or reads the
private source-progress store.

The operator-only council route `/model-authority` accepts the same capability
request. It invokes `RuntimeVerbs.OpenPreviewModelsAsync` and the shared
`RuntimeWorkflow`, then the real model-consumption host. Its
`PreviewModelAuthorityContext` binds the operator request, capability revision,
and fresh native observations for synthesis, embedding and all three jurors.
It issues no inference probe and is not aggregate preview-input readiness.

Success is HTTP 200 with `PreviewSourceContext`: matching request/operation/
approval, `capabilityRevision`, and current `SourceCharterContextResult`.
This includes exact adopted charter, complete analyzed source and uncertainty,
original inventory integrity, current reconciliation, and native model authority.
It is **not a completed council evaluation, readiness proof or activation**.
Noncurrent source results retain explicit Partial/Blocked/Failed/Canceled/Unknown
state without context. Admission and bounded failures use
`PreviewContextFailure` (`state`, sanitized `reason`), never provider diagnostics.
Requests are limited to 4 KiB, complete responses to 4 MiB; overflows fail rather
than truncate. The outer request budget is 180 seconds, enclosing the existing
150-second source-reading budget and capability checks. Responses use `no-store`.

The current capability is stored in product App Configuration, not the owner
registry. No runtime principal receives governance-write permission.

| Protected record | Contract |
| --- | --- |
| `dsf/factory/preview` | Protocol 1 `FactoryPreviewCapability`, kind `decide-preview-capability`, explicit `Prepared` state, operation, approving Azure operator/time, configuration hash and all effect hashes |
| `dsf/factory/preview-bindings/{operation:D}/{name}` | Original completed `FoundationOperation`; names `council`, `code`, `hosting`, `account`, `database`, `runs` |
| `dsf/factory/preview-operations/{operation:D}/{index:D4}` | Completed creation receipt, kind `decide-preview-operation`, exact target/API, request hash, provider IDs, deployment operation and current native revision |
| `dsf/factory/preview-supplemental/{operation:D}` (selected sources only) | Exact `PreviewSupplementalConfiguration`, pinned by `configuration.supplementalRevision` in the capability |
| Factory manifest | Both `previewOperationId` and `previewApprovalFingerprint` must match the protected capability and request |

Preview records are unlabeled and use content type
`application/vnd.dsf.preview+json;version=1`. Native record metadata, including
ETags, is bounded and retained in revision comparisons; replacing identical
content with a different ETag invalidates an in-flight read. Missing records,
fields, labels, record types or native receipts cannot default to approval.

`FactoryPreviewConfiguration` pins protocol/purpose
`supervised-proposal-preview-v1`, factory/profile/plan, the manifest basis
(excluding only its two preview pins), source and model configuration/deployment
revisions, adopted-charter revision, immutable runtime image, audience client ID,
metadata role definition/revision, and all six original binding hashes.
Source/model routes and model proof still undergo their existing current
validation; a retained proof is not substituted for live native authority.
Both hosts recheck capability before releasing successful context. Source
revision/fetched-charter/adoption fences remain inside the actual shared reader.

#### Verified council model consumer

`FactoryPreviewConfiguration.modelEvaluation` is an optional **protected**
`PreviewModelHostConfiguration`, with exact `purpose`
`council-model-evaluation-v1`, immutable `runtimeImage`, and
`audienceClientId`. Omission preserves the source-only capability; it cannot
authorize model consumption. Selection is included in the configuration
fingerprint, manifest approval pin, deployment receipts and native host checks.
The existing model topology/configuration/deployment pins remain mandatory.

`RuntimeVerbs.OpenPreviewModelsAsync` returns `IFactoryPreviewModelWorkloads`,
implementing the existing `IFactoryModelWorkloads`: async singleton-or-multiple
embedding/deduplication, synthesis (also used by grounding/lenses), and each of
the three juror routes. It adds request-bound `ReadAuthorityAsync` and bounded
`Observations`. The client obtains its own COUNCIL token and sends
`PreviewModelRequest` to the approved `/evaluate` host; it never dispatches to a
model account directly.

The model host uses the actual `FactoryModelWorkloads` implementation under
COUNCIL's existing reviewed narrow model grants. It verifies all five native
account/deployment/role/assignment routes before inference and again afterward,
and rechecks protected capability/native deployment bindings before release.
Client and host retain and validate request ID, input hash, capability revision,
current model authority, actual response model/configuration, output hash,
dimensions and complete embedding groups. A singleton sends **one** input to
native embeddings; no repeated observation or lexical substitute.

Model requests/responses are limited to 128 KiB, text inputs to 32,768
characters, outputs to 8,192, embedding inputs to 1..32. Native model responses
are streamed within 1 MiB (text/metadata) or 4 MiB (embeddings), not buffered
unboundedly before checking size. Admission and each consumer operation have
180-second deadlines. A consumer retains at most 64 confirmed observations;
overflow fails explicitly. Unknown/canceled dispatch or malformed returned
provenance prevents further dispatch on that consumer instance. This is not a
durable dispatch journal, a cross-process retry fence, a complete candidate
evaluation or a readiness proof.

The model consumer does not replace current-evidence or adopted-intent gates.
The aggregate `CouncilEvaluationContext` operation remains separate integration work.

#### Genuine application telemetry consumer

The operator-only `POST /telemetry-evidence` on `serve-preview` invokes
`RuntimeVerbs.ReadPreviewTelemetryAsync` and shared `RuntimeWorkflow`. COUNCIL
obtains its own audience-bound token and calls **COUNCIL-only** `POST /evidence`
on the new `serve-preview-telemetry` host. That host runs under the original
TELEMETRY UAMI (`factory:source-azuremonitor`), not COUNCIL or CODE. It uses
`FactoryTelemetryQuery.ObserveEvidenceAsync`, sharing the actual native query,
schema, row attribution, freshness, complete-table checks and proof machinery
with the existing operator `/proof`. The coordinator receives no telemetry
credential or backend grant. No inference, issue/PR/label mutation, handoff,
state write or activation occurs.

Both routes accept only the existing `PreviewCapabilityRequest`
(`operationId`, `approvalFingerprint`, `requestId`), not queries or content.
`FactoryPreviewConfiguration.telemetry` is a protected
`PreviewTelemetrySelection`. Milestone 3 must present and approve **all** of:

| Pin | Required value |
| --- | --- |
| `purpose` | `council-telemetry-evidence-v1` |
| `configurationRevision` | Exact existing #208 telemetry configuration fingerprint: application environment/resources, all backends/workspaces/tables, source/hosting bindings, original query revision and freshness |
| `deploymentRevision` | Hash of the complete original telemetry creation receipts |
| `sourceIdentity`, `applications` | Exact original source `FoundationOperation`; sorted distinct selected backend Application Insights resource IDs |
| `runtimeImage`, `audienceClientId` | Separately reviewed immutable evidence-host image and native Entra audience |
| `projectionRevision` | `azuremonitor-app-content-v1` |
| `rowsPerTable`, `queries` | Explicit latest-row sample size and complete exact content-query dictionary, separate from original #208 queries |
| `metadataRoleDefinitionId`, `metadataRoleRevision` | Exact custom TELEMETRY metadata role and native properties hash |
| `accessRoleRevisions` | Native properties hashes for every original telemetry access-role definition |

Missing selection is blocked as
`PreviewTelemetryProjectionRequired:dsf/factory/preview.configuration.telemetry`.
An unapproved or changed selection cannot inherit approval. Current preparation
must be `Ready`/`observed`, with complete original effects and matching persisted
last-check/verified-proof references and hashes. Missing/corrupt receipts remain
Unknown; explicit source Partial/Failed/Canceled/Unknown states are preserved.
The receipt is historical integrity, **not current evidence authorization**:
new native queries and current permission checks are always required.
The operator-persisted telemetry root has `proof: null`; the independent proof
record is validated using `FactoryTelemetryValidation.RequireVerifiedHistory`.

The first-party content projection supports only these fields in addition to
native `TimeGenerated`, `_ResourceId`, `_ItemId`, `TenantId`, and `Type`:

| Analytics table | Content fields |
| --- | --- |
| `AppRequests` | `Name` string, `Success` bool, `ResultCode` string, `DurationMs` real |
| `AppDependencies` | `Name` string, `DependencyType` string, `Success` bool, `ResultCode` string, `DurationMs` real |
| `AppExceptions` | `ExceptionType` string, `OuterMessage` string |
| `AppTraces` | `Message` string, `SeverityLevel` int (0..4) |
| `AppEvents` | `Name` string |

For example, the exact separately approved two-row request query is:

```kusto
AppRequests | top 2 by TimeGenerated desc | project TimeGenerated, _ResourceId, _ItemId, TenantId, Type, Name, Success, ResultCode, DurationMs
```

The native API timespan is `[now - freshnessSeconds, now]`. No application
filter is added to hide foreign rows. Empty tables use the **original**
metadata-only stale probe; it never creates content. Every selected table must
be checked, with at least one fresh attributable content record per backend.
An empty table can coexist with usable records in another selected table;
an empty required backend cannot. `TenantId` in these native rows is the
workspace/customer ID; Entra token tenant validation is separate.

All content is **untrusted application input**, never instructions or proof of
semantic support. Null/absent/type-mismatched content, protected/non-Analytics
tables, unsupported workspace authorization modes, wrong attribution,
duplicate references, partial/error/malformed responses and excess rows fail
explicitly. Strings are limited to 2,048 JSON bytes per field, nonblank, without
unsupported controls; durations must be finite/nonnegative. Conservative
screening rejects recognized credentials, bearer/JWT material, email/IP
addresses, absolute URLs and query-string content, without echoing it.
There is no silent redaction or substitute summary. This screening is **not
universal PII/DLP detection**; the operator must approve the data exposure and
select only suitable first-party fields. Custom properties, users, sessions,
URLs, dynamic payloads and arbitrary projection expressions are unsupported.

`rowsPerTable` is 1..8, with at most 32 potential rows across all selected
backend/table pairs and at most eight distinct application components. This
is an explicit latest-row **sample, not exhaustive telemetry**. Native
truncation/partial errors cannot masquerade as that sampling policy. Native
query/metadata bodies and the complete evidence envelope are streamed/bounded
to 128 KiB; no unbounded SDK response buffering before the check. Requests are
4 KiB and the complete operation is bounded to 180 seconds. Overflow yields
Partial, not truncated completeness. No automatic query retry.

HTTP 200 returns `PreviewTelemetryEvidence`: request ID, protected-record
`governanceRevision`, source-native `sourceAuthorityRevision`,
`selectionRevision`, and `TelemetryEvidenceCapture`. The capture contains
actual typed content fields, exact row references and timestamps, content and
query hashes, native query observations/request IDs, current source proof,
and the complete configured native metadata/grant/reference revision map.
Source and coordinator check request/pins, whole-result integrity and freshness
before release. Routine new telemetry rows do **not** change configuration
authority or invalidate still-fresh captured rows.

The source rechecks current workspace/component mapping, table policy,
access-role definitions and direct/inherited grants before/after collection.
Both hosts fence protected-record/native-host changes; TELEM checks its own
identity and metadata scopes, while COUNCIL checks factory/council/own-state
and participating-host bindings. `governanceRevision` is common to these
different least-authority native views; `Revision`/`sourceAuthorityRevision`
also include the consumer's actual native observations. These are sequential
point-in-time checks, not cross-provider fencing or global discovery of all
tenant grants/components. Dedication remains owner-reviewed.

An unusable native query returns a bounded non-Ready capture (HTTP 409) with
**no content rows** and its reason. Authority/transport/provenance failures
return sanitized `PreviewContextFailure`, preserving uncertainty. A source
proof's `Ready` state means usable telemetry only: it is **not council
readiness, a completed preview or activation**. This read-only operation does
not yet persist preview evidence; checkpoint 2c must persist its bounded
provenance with the actual conveyor run.

#### Selected supplemental consumption

Operator-only `POST /supplemental-evidence` invokes
`RuntimeVerbs.ReadPreviewSupplementalAsync` and shared `RuntimeWorkflow`.
It accepts only `PreviewCapabilityRequest`, never caller-authored context or
queries. COUNCIL checks current protected/native capability, calls **every**
selected source's new `/evidence` using its own audience token, validates each
response, and rechecks capability and proof age before releasing results.
The source request is `PreviewSupplementalRequest` (`preview`, `kind`).
Existing operator `serve-supplemental` `/proof` and `/evidence` stay unchanged.

Each new host runs the original `FactorySupplementalEvidence` workload under
its own original FoundryIQ or WebIQ UAMI. These identities have **no product
governance read grant**. Instead, the source calls COUNCIL
`/supplemental-authority` with its own token before and after retrieval.
COUNCIL validates current protected/native bindings and that exact requested
source's principal/client. `PreviewSupplementalAuthority` returns only its
own typed workload, original identity/hosting/vault/grant receipts, relevant
Search prerequisite hash, selected metadata grant, and request/governance/
capability revisions. It contains no charter, analysis, telemetry, other
source configuration, operator token or secret value. A source cannot use
operator routes or read another source's authority.

The capability pins `supplementalRevision`, the hash of the separate protected
`PreviewSupplementalConfiguration`. That record pins original #213
`configurationRevision`, complete original `deploymentRevision`, and the
ordered, complete selected host list. Each `PreviewSupplementalHost` carries
`kind`, original identity receipt hash (`identityRevision`), `principalId`,
`clientId`, separately approved immutable `runtimeImage`, `audienceClientId`,
`metadataRoleDefinitionId`, `metadataRoleRevision`, and exact `metadataScope`.
The shared capability reader resolves and verifies this record from the same
fetched snapshot; its in-memory `Supplemental` property is not serialized as
another authority. Splitting this record avoids exceeding App Configuration's
10 KiB per-setting limit when telemetry, models and both sources are selected.
No record bound is increased and missing references cannot disable a source.

Original preparation must be current `ReadySupplemental`/`observed`, with all
effects and independent per-source proof records matching their protected
hashes. Historical proofs establish receipt integrity, not current evidence.
Original direct identity/grant creations retain native request/correlation
receipts without invented deployment-operation IDs; original host/auth
receipts must name their actual `sp-{operation:N}-{kind}` deployment and
provider operation. Preview overlay effects remain separately pinned.

Before and after the real query, the source checks its current private/RBAC
backend, original narrow data/metadata grants, exact role definitions, and
direct/inherited grant inventory at the approved scope, allowing only the
separately approved preview metadata overlay. WebIQ reads the exact
operator-seeded product `webiq-api-key` **version** before and after retrieval;
no environment/owner-key fallback. FoundryIQ revalidates the exact dedicated
Search service, extractive knowledge base, selected `searchIndex` sources,
indexes, ETags, activities and supported permission modes. No crawl, model
query or provider-URL following is added.

HTTP 200 returns `PreviewSupplementalEvidence`: request ID, common protected
`governanceRevision`, supplemental state, and one
`PreviewSupplementalSourceEvidence` per selected source. Each source contains
its current authority, actual `SupplementalEvidence` (content, opaque/scoped
references and request-bound proof), and complete native metadata revision map.
Web references remain `webiq:sha256:...`; provider document IDs and URLs are
not exposed or followed. Existing source limits, key-echo rejection and
conservative secret screening remain in force; content is untrusted evidence,
not instructions or demonstrated semantic support.

A selected native `[]` is `ReadySupplemental` with reason `Empty`. Null,
missing, malformed, partial, denied and unsupported results are never empty.
Non-Ready source proofs retain their state/reason with no content; a later
selected source is still checked. Authority/transport failures withhold the
capture as sanitized Blocked/Partial/Failed/Canceled/Unknown, not fabricated
source proofs. Missing root/pins are `NotSelected` only after current
all-label enumeration confirms the supplemental namespace is pristine:
orphaned effects/proofs/configuration or unreadable/labeled history are Unknown.
Pristine Azure-only requires no supplemental host, grant, setting or upstream
call; the namespace read uses COUNCIL's existing governance-read permission.

Requests are 4 KiB, authority callbacks 32 KiB, each source envelope/native
metadata body 256 KiB, and combined output 1 MiB. Existing #213 limits remain
16 items per source, 8,192 characters per item, 320-character references and
8 KiB proofs; native bodies are streamed and bounded, including unknown
Content-Length. Whole consumption has a 180-second budget, enclosing the
existing 60-second source workload and two-minute proof validity. Overflow
fails rather than truncates. These sequential checks are not global grant
discovery or cross-provider atomic fencing.

This operation is read-only: no publication, activation, source removal,
configuration repair, checkpoint or readiness claim. Aggregate trusted inputs,
bounded combined evidence selection, actual conveyor execution and durable
readiness proof remain subsequent work.

#### Reviewed deployment contract

Let `G` be the original factory resource group, `F` the factory GUID in lowercase
32-character form, `P = df-{first 14 characters of F}`, and `K` the product key.
The operator-reviewed plan must explicitly create **new** Container Apps
`G/providers/microsoft.app/containerapps/{P}-preview` and `{P}-context`.
Original source/model hosts, images, caller lists and creation receipts remain
unchanged: `/charter-review` remains operator-only; `/materialize` remains
CODE-only. Legacy model `/proof` remains operator-only; synthesis, deduplication
and authority remain CODE-only; existing juror callers remain unchanged.
Original telemetry `/proof` and original supplemental `/proof` and `/evidence`
remain operator-only and unchanged.

When `modelEvaluation` is selected, the same reviewed plan additionally creates
`G/providers/microsoft.app/containerapps/{P}-preview-models` and its native auth
resource. This third host uses its separately pinned image/audience, the
original COUNCIL UAMI and `serve-preview-models`; its only native caller is
COUNCIL. It has the same four environment names, isolated host shape and
private environment requirements below. The existing source/council images and
the new model image must all appear explicitly in the operator-reviewed plan.

Selecting `telemetry` additionally creates
`G/providers/microsoft.app/containerapps/{P}-preview-telemetry` and
`.../authconfigs/current`. Use the separate selected image/audience, **original
TELEMETRY UAMI**, `serve-preview-telemetry`, and only the original COUNCIL
principal as native caller. The same host-shape restrictions and four settings
apply, with `AZURE_CLIENT_ID` set to TELEMETRY's original client ID. The
COUNCIL host image must explicitly include `/telemetry-evidence` in the reviewed
plan; neither this nor any source/model image change is implicit.

For **each selected** supplemental source, explicitly create
`G/providers/microsoft.app/containerapps/{P}-preview-{kind}` and its
`authconfigs/current`, with that source's original distinct UAMI, separately
reviewed image/audience and `serve-preview-supplemental`. Its native caller is
only COUNCIL. The COUNCIL preview host's reviewed image must include the two
supplemental routes; its native allowed-principal list becomes the operator
**plus only selected supplemental principals** to permit authority callbacks.
Application authorization keeps all operator routes operator-only and
callbacks source-kind/client-bound. This is a reviewed preview-host policy,
not implicit image drift or an expansion of legacy inactive endpoints.

Each new host must use the approved immutable image, original private managed
environment, one assigned UAMI, `dotnet dsf-runtime.dll`, and its exact command
from the table. Native ACA auth must require HTTPS/Entra/Return401, the exact
tenant, both GUID and `api://GUID` audiences, and only the listed principal.
No excluded paths, groups, token store, extra identities, secrets, mounts,
init containers, service bindings or Dapr. Ingress port is 8080; revision mode
is Single. Source/model/telemetry hosts have exactly these four settings;
selected supplemental hosts have these four plus the two below:

```text
DSF_PRODUCT=K
AZURE_APPCONFIG_ENDPOINT=https://dsfo-F.azconfig.io
DSF_FACTORY_PROFILE_PIN=<original protected profile pin>
AZURE_CLIENT_ID=<that host's original CODE, COUNCIL, TELEMETRY or selected supplemental client ID>
```

Selected supplemental hosts additionally require:

```text
DSF_SUPPLEMENTAL_CONFIG=<exact original #213 SupplementalHostConfiguration for this source; at most 8 KiB>
DSF_PREVIEW_COORDINATOR=<PreviewSupplementalCoordinator JSON; at most 2 KiB>
```

The coordinator JSON has `endpoint` (the exact HTTPS private COUNCIL ACA
endpoint), `audienceClientId` (COUNCIL preview audience), `principalId` and
`clientId` (original COUNCIL UAMI). COUNCIL revalidates both environment values
against current native host/configuration receipts. These settings carry no
credentials and grant the source no App Configuration access.

COUNCIL retains its already approved product-config Data Reader assignment.
Current product-config metadata must still show the exact endpoint, successful
provisioning and disabled local/access-key authentication. Its existing
operator-accessible public networking is preserved, not replaced with a new
private-only requirement.
It additionally needs an operator-approved, hash-pinned **custom ARM metadata
role** containing exactly these actions and no data actions, exclusions or
wildcards:

```text
Microsoft.App/containerApps/read
Microsoft.App/containerApps/authConfigs/read
Microsoft.App/managedEnvironments/read
Microsoft.AppConfiguration/configurationStores/read
Microsoft.ManagedIdentity/userAssignedIdentities/read
Microsoft.DocumentDB/databaseAccounts/read
Microsoft.DocumentDB/databaseAccounts/sqlDatabases/read
Microsoft.DocumentDB/databaseAccounts/sqlDatabases/containers/read
Microsoft.DocumentDB/databaseAccounts/sqlRoleDefinitions/read
Microsoft.DocumentDB/databaseAccounts/sqlRoleAssignments/read
Microsoft.Authorization/roleAssignments/read
Microsoft.Authorization/roleDefinitions/read
Microsoft.Resources/deployments/read
Microsoft.Resources/deployments/operations/read
```

Assign this role at **ten exact resource scopes**, not a group or subscription:
COUNCIL UAMI, CODE UAMI, managed environment, factory Cosmos account, original
source host, original model host, product App Configuration, new preview host,
new context host, and
`G/providers/microsoft.resources/deployments/preview-{operation:N}`.
With model evaluation selected, add only the eleventh metadata scope: the new
`{P}-preview-models` Container App. The action set remains unchanged, with no
new direct model-account, source-backend, secret or governance-write grant.
Role definitions are read through an authorized resource scope; no
subscription-wide role-definition read is required. Current direct/inherited
assignments at those scopes are checked for unapproved access. CODE retains
its existing factory metadata/source-progress rights; no new CODE grant or
application-backend access is required.

Telemetry selection adds **three** exact scopes for COUNCIL's existing
14-action metadata role: original TELEMETRY UAMI, original `{P}-telemetry`
host, and new `{P}-preview-telemetry` host. No workspace/component data or
metadata grant goes to COUNCIL.

Each selected supplemental adds **three** COUNCIL metadata scopes: its
original UAMI, original `{P}-{kind}` host, and new `{P}-preview-{kind}` host.
COUNCIL gains no Search, Key Vault, secret or supplemental data permission.
Each source retains its original #213 grants and receives exactly **one**
separately approved, hash-pinned custom ARM metadata assignment:

| Identity | Exact assignment scope | Exact additional actions (no data actions, exclusions or wildcards) |
| --- | --- | --- |
| FoundryIQ | Selected Search resource ID | `Microsoft.Authorization/roleAssignments/read`, `Microsoft.Authorization/roleDefinitions/read` |
| WebIQ | Original product vault ARM resource ID | The same two authorization reads plus `Microsoft.KeyVault/vaults/read` |

FoundryIQ retains Search Index Data Reader
`1407120a-92aa-4202-b7e9-c0e197c71c8f` and its original custom role containing
exactly `Microsoft.Search/searchServices/read`, `indexes/read`,
`knowledgebases/read` and `knowledgesources/read` under that provider/service.
WebIQ retains Key Vault Secrets User
`4633458b-17de-408a-b874-0445c86b69e6` only at
`{product-vault}/secrets/webiq-api-key`, not the vault or other secret.
No source gets group/subscription-wide role inventory, product governance,
source progress, model, repository, Cosmos or another source's privileges.
Role definitions are read through authorized resource scopes; their reviewed
assignability does not itself grant access.

Supplemental role reads use `2022-04-01`; Search resource metadata uses
`2025-05-01`, and vault metadata uses the original pinned vault API version.
Identity reads use `2024-11-30`, ACA/auth `2025-01-01`; original Foundry data/
metadata uses `2026-04-01`, and versioned Key Vault secret reads use `7.4`.
WebIQ retains `POST https://api.microsoft.ai/v3/search/web` with the original
fixed public-safe query and source-owned `x-apikey`; no private context is sent.
Keep the existing native networking/reachability prerequisites; no network
repair or endpoint fallback is implicit.

TELEMETRY retains its original product App Configuration Data Reader
(`516239f1-63e1-4d78-a4de-a74fb236a071`) and each selected workspace's
Monitoring Data Reader (`73c42c96-874c-492b-b04d-ab87d138a893`). Those exact
role definitions/assignments are revalidated; no extra backend data grant
is introduced. It additionally needs this separately approved custom ARM role
with **exactly nine actions**, no data actions, exclusions or wildcards:

```text
Microsoft.App/containerApps/read
Microsoft.App/containerApps/authConfigs/read
Microsoft.App/managedEnvironments/read
Microsoft.AppConfiguration/configurationStores/read
Microsoft.ManagedIdentity/userAssignedIdentities/read
Microsoft.Authorization/roleAssignments/read
Microsoft.Authorization/roleDefinitions/read
Microsoft.Resources/deployments/operations/read
Microsoft.Insights/components/read
```

Assign it to TELEMETRY at seven exact factory scopes: its own original UAMI,
COUNCIL UAMI, managed environment, product App Configuration, original
`{P}-telemetry`, new `{P}-preview-telemetry`, and the exact preview deployment.
Also assign it at **each selected Application Insights component resource**,
solely for current component-to-workspace mapping and scoped grant metadata.
The custom role's reviewed `assignableScopes` must cover these actual factory
and application resources, but assignments must never be group/subscription
wide. `FactoryPreviewTelemetryValidation.MetadataScopes` enumerates the exact
resource scopes; assignment IDs use the existing deterministic
`FactoryModelsValidation.Grant` contract. Native grant creation receipts belong
to the same preview operation.

Workspace/table metadata reads use `2026-03-01`; component reads use
`2020-02-02`; ARM role reads use `2022-04-01`, ACA/auth use `2025-01-01`,
and deployment-operation reads use `2022-09-01`. Workspaces must retain their
selected customer IDs, `enableLogAccessUsingOnlyResourcePermissions=false`
and `dataAuthorizationMode=false`; tables must be General/Analytics.
TELEMETRY needs neither CODE/source-progress/Cosmos/model-account privileges
nor subscription-wide assignment/component inventory. Current grant inventory
is scoped to the reviewed resources, not a claim of global exclusivity.

Factory Cosmos account `G/providers/microsoft.documentdb/databaseaccounts/{P}-cosmos`
must retain its original incarnation, disabled local auth and disabled public
networking. Its original SQL database is `K`, with existing `runs` and new
`council-preview` containers, both partitioned `/product` (Hash, version 2).
Create one custom SQL role with account `assignableScopes` and exactly:

```text
Microsoft.DocumentDB/databaseAccounts/readMetadata
Microsoft.DocumentDB/databaseAccounts/sqlDatabases/containers/items/read
Microsoft.DocumentDB/databaseAccounts/sqlDatabases/containers/items/create
Microsoft.DocumentDB/databaseAccounts/sqlDatabases/containers/items/replace
```

Assign it to COUNCIL only at `{account}/dbs/K/colls/runs` and
`{account}/dbs/K/colls/council-preview`. No query, delete, upsert, other-container
or account-wide item grant. `FactoryPreviewValidation` supplies deterministic
resource/SQL-role/assignment IDs and the complete 18-receipt target list:
container, SQL role, two SQL assignments, ten metadata assignments, and four
host/auth resources. Selecting model evaluation produces **21** receipts:
eleven metadata assignments and six host/auth resources, with the other four
resources unchanged. With `A` distinct selected application components,
telemetry adds `12 + A` receipts: three COUNCIL metadata assignments, two
evidence host/auth resources and `7 + A` TELEMETRY metadata assignments.
Thus source+telemetry needs `30 + A` receipts, or `33 + A` with model evaluation
(31/34 respectively for one component). Original #208 receipts remain separate
and pinned. `FactoryPreviewValidation.Targets` and `MetadataScopes` must receive
the approved, resolved configuration to enumerate the selections.
Each selected supplemental adds **six** preview effects: three COUNCIL
metadata assignments, two source host/auth resources, and one source-owned
metadata assignment. With models, telemetry and both selected supplementals,
the total is `45 + A` (46 for one component), plus the separate bounded
protected supplemental configuration record. No selected sources means none
of these records/resources/grants/settings.
Receipt indices are not deployment execution order:
resource-scoped grants depend on their target resources existing.

Milestone 3 must wire these exact approved resources, caller policies,
configuration records and native receipts into the deployment plan/assets.
The existing operator #208/#213 native isolation checks must recognize only
the corresponding separately approved, receipt-backed preview metadata
overlays; their original rejection of arbitrary extra grants must not be
relaxed. This slice changes neither CLI/provisioning assets nor live resources.

Actual ARM deployment operations must report Create/Succeeded with matching
target, tracking/operation IDs and Created/201 or OK/200 status. Native
`systemData`, creation ETag, and Cosmos `_rid` forms are supported; SQL role
resources are checked by exact properties and deployment receipts, not invented
timestamps. These are point-in-time checks, **not cross-provider atomic fencing**,
global tenant permission discovery or a proof of successful Cosmos data-plane
persistence. This path writes no governance, source progress, preview/run state,
GitHub issue/PR/label, handoff or activation. Aggregate evidence acquisition,
conveyor composition and persisted readiness proof remain separate integration
work; generic inactive `run`, `sweep`, HTTP and dry-run admission is unchanged.

**See also:** the [loop overview](the-loop.md), the next phase
[Creation phase](creation.md), and the decision-path redesign in
[ADR 0011](https://github.com/JoranBergfeld/dark-software-factory/blob/main/docs/adr/0011-feature-council-deliberative-redesign.md)
and its
[design spec](https://github.com/JoranBergfeld/dark-software-factory/blob/main/docs/superpowers/specs/2026-06-19-feature-council-deliberative-redesign-design.md).
