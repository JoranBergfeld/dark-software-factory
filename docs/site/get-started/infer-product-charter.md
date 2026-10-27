# Infer a grounded product charter

`dsf onboard decide infer` asks the inactive factory to infer a **private,
review-only** charter from its complete pinned source acquisition. Operators
review purpose, intended users, substantive goals, original-source support,
unknowns and contradictions rather than supplying a product description.
Nothing is written to the repository, adopted as governing intent, or activated.

## Prerequisites and execution

Complete [source acquisition](acquire-source-inventory.md) and obtain current
[verified model capabilities](prove-model-capabilities.md). Both hosts must use
digest-pinned images built from this implementation. The source host's existing
code identity calls the approved model host's `/synthesis` route; the model host
uses its separate inference identity. Operator credentials never call the model
provider. Existing native Entra consent and private connectivity must work.

The model image must also implement code-only `POST /authority`, protocol 1.
The code host uses this read-only route to check all five approved native
accounts/deployments, exact inference assignments, role definitions and workload
identity against their original provenance. It does not gain model-account ARM,
inference or additional store permissions: the model host performs those reads
under its existing model identity using the ordinary workload route verifier.
An unexpired stored proof cannot substitute for these current checks.

Older model images without this route block with `NativeModelAuthorityUnavailable`.
Installing the tool does not upgrade a deployed image. Use compatible, reviewed
digest-pinned images; inference never repairs consent, mutates create-once
infrastructure, recreates hosts or accepts inherited access as replacement
authority.

No new identity, store, grant, signing-key access or governance writer is added.
The already isolated `dsfs-<factory-id-N>` store holds two separate purposes:
`source/*` acquisition metadata and `intent/*` derived analysis metadata. Its
code-only Data Owner grant never extends to product governance or owner records.
`onboard-source.json` provisions the shared code host/store; `onboard-models.json`
provisions the separately authenticated model workload. Installing the tool
includes those assets and the runtime implementation, not an automatic image
upgrade or consent repair.

Interactively, run `dsf onboard decide infer`. Missing owner endpoint, product,
tenant and subscription are prompted; the equivalent explicit command is printed.
There is no question asking you to invent purpose, users or goals. Each invocation
does bounded work and returns current private review data.

Fully explicit invocation, also suitable for redirected terminals:

```bash
dsf onboard decide infer \
  --owner-appconfig-endpoint https://owner.azconfig.io --product widget \
  --tenant 11111111-1111-1111-1111-111111111111 \
  --subscription 22222222-2222-2222-2222-222222222222 --json
```

Replace illustrative values with your reserved factory locators. Repeat this
same command to advance compatible work. There is no automatic background loop.
`--json` and redirected terminals never prompt or infer missing inputs.

Read-only inspection, including private evidence pages:

```bash
dsf onboard decide infer --status-only --page 0 \
  --owner-appconfig-endpoint https://owner.azconfig.io --product widget \
  --tenant 11111111-1111-1111-1111-111111111111 \
  --subscription 22222222-2222-2222-2222-222222222222 --json
```

Follow `nextPage` for analysis records and `nextExclusionPage` for exclusions
using the same command with that `--page`. Each record includes its own findings,
limitations and, during verification, every claim judgment. Later summary pages
do not erase earlier limitations. Status never writes, repairs or calls a model.
It does recheck current authority, source identity and whole-inventory integrity.
Removing a source current checkpoint while any source namespace records survive
is `Unknown`, even if a completed intent draft survives. Recorded source
uncertainty remains `Unknown`; genuinely pristine missing acquisition is
`Blocked`. Neither case repairs state, reacquires source or replays analysis.
Acquisition, status, analysis inventory and chunk materialization all share this
current-checkpoint loss check.

## What completion means

The runtime performs two passes over **every acquired relevant chunk**, including
tracked hidden files, tests and supporting documentation:

1. Infer a bounded candidate from each original chunk and the previous candidate.
   Persist that chunk's substantive findings, candidate, dependencies and actual
   model observation. Do not treat an acquired flag as analyzed coverage.
2. Freeze the final candidate and reacquire every original chunk again. Ask for
   semantic support, contradiction, irrelevance or insufficient support for every
   material claim, including recorded authority and currency. Require original
   excerpts and explanations; citation membership alone cannot establish support.

Every declared citation must receive semantic support at its original source;
unsupported or contradictory material claims block `DraftReady`. Checking every
original against the final candidate prevents earlier summary compression from
silently hiding counterevidence. UTF-8 scalars split across fixed byte ranges are
reassembled with explicit adjacent-chunk dependencies.

Output distinguishes `ImplementedBehavior`, `InferredPurpose`, and
`RecordedIntent`. Authority and currency may explicitly be `Unknown`; a document
does not authenticate its own strategic authority. Conflicts retain source and
rationale for human resolution. `deployedSourceCorrelation` remains `Unknown`:
source and unexecuted tests are not observed production behavior.

The shared `grounded-onboarding-draft-v1` contract requires substantive purpose,
intended users and at least one substantive goal. Non-goals and success measures
are either known with substantive values or explicitly unknown with a reason and
an empty values list. Unknown markers are not goals or fabricated metrics.
This is **not** schema-1 charter Markdown: the existing seven-heading parser and
its required success metric remain unchanged. Non-goals do not become a
deterministic veto.

`acquisition` reports independent source coverage; `progress.availableChunks`,
`analyzedChunks` and `checkedChunks` report analysis coverage. `excludedPaths`
lists source exclusions separately, never as processed content. Necessary source
gaps prevent any model analysis. Inspect the source command's complete coverage
when acquisition is incomplete.

## Outcomes and recovery

| State | Meaning |
| --- | --- |
| DraftReady | Both passes complete, meaningful core intent and supported claims; exit 0, owner review only |
| Partial | Pending analysis/checks or explicit time, prompt, claim, attempt or metadata limit; exit 1 |
| Blocked | Missing/revoked authority, incompatible pins, unsupported necessary source, unsafe output or conflicted/unsupported intent; exit 1 |
| Failed | Definite malformed model output, invalid original support or processing failure; exit 1 |
| Canceled | Caller cancellation; already-issued work may remain unconfirmed; exit 130 |
| Unknown | Unconfirmed dispatch/write/response, missing or changed state, or uncertain final authority; exit 1 |

The source host authenticates operator-only `POST /infer` and
`POST /intent-status`; code-only `/materialize` remains unchanged. Each dispatch
is conditionally recorded before model access, with a unique ID, exact input
hash and verified capability reference. Confirmed immutable receipts can be
reconciled on an independent worker without repeating the model call.
An unresolved dispatch stays held: expiry, cancellation, a missing response,
or `--new-analysis` cannot authorize another call. Unknown writes never assert
that an old authorization was revoked.
Source uncertainty after a model response retains the original unconfirmed
dispatch and provenance, not a known model failure. Restoring source acquisition
cannot authorize replay. If analysis receipts were already confirmed before a
final source check becomes uncertain, those receipts stay confirmed but the
draft is withheld until current source authority is re-established.

Model configuration or source selection changes cannot mix with existing work.
After resolving source acquisition and model proof for the new selection, use
the full explicit invocation above with `--new-analysis` to start another bounded
revision. It preserves historical metadata and cannot replace an unresolved
dispatch. Proof renewal under the same model configuration can resume compatible
work; each actual observation retains its own proof/configuration provenance.
There is no reset, force, infrastructure recreation, public-access fallback or
automatic recovery from irreconcilable unknown work.

Before releasing results, the host rechecks source and delegated product/model
authority; the CLI also rechecks the final owner registry. `governs` is always
false. Retained successful data is not current authority when those checks fail.
`modelAuthority` exposes the final request-bound native check: protocol, fresh
check ID, capability reference, model principal and all five routes' exact
account/deployment/grant observations and role revisions. A replayed check ID,
missing route, revoked/replaced grant or changed native configuration withholds
the draft without overwriting its historical evidence.

These are current **control-plane** checks, not new inference evidence or renewed
capability proof. They perform twenty bounded native GETs per authority request,
no model completions, embeddings or state writes, within the enclosing deadline.
They do not prove current data-plane availability, quota or inference-network
reachability; actual model calls retain their own checks and explicit failures.
Native reads across Azure resources are not an atomic revocation transaction.
Lost authority responses remain `Unknown`, with provider details withheld.

## Bounds, storage and limitations

| Bound | Limit |
| --- | --- |
| New model calls per advance | 1 |
| Host / CLI deadline | 120 / 150 seconds |
| Whole source integrity scan | Existing 45-second, 1024-page source protocol |
| Relevant acquired chunks per analysis revision | 512; larger snapshots stay incomplete, never sampled |
| Successful passes | 2 calls per chunk |
| Total attempted model calls per revision | Twice available chunks plus 16 |
| Material candidate claims | 8, each with 1..2 original references |
| Claim / evidence explanation / excerpt | 256 / 256 / 96 characters |
| Findings / limitations per chunk | 512 characters / four entries of at most 256 characters |
| Model prompt / native completion | 32,768 / 8192 characters; oversize or truncation is explicit failure/incompletion |
| Evidence and exclusion page | 8 entries each |
| Analysis revisions | 8, including incomplete revisions |
| Analysis namespace | 65,536 records and 64 MiB across the whole retained lifecycle |
| Each record with conservative key/content-type/metadata overhead | 8192 bytes, below App Configuration's 10 KiB limit |

Acquisition independently retains its original 64 MiB budget, so the two bounded
namespaces reserve at most 128 MiB together. New analysis does not enlarge,
reinterpret or rewrite source inventory records or integrity commitments.
Namespace scans and conditional native ETags protect cooperating workers;
per-key ETags are not cross-record transactions against administrator mutations.

`intent/initialized` and `intent/current` prevent missing progress from looking
pristine. The current root conditionally holds a `pendingSelection` before
archiving a prior revision; interrupted selection resumes that exact revision,
not old work or another randomly generated ID. Immutable history retains each
selection's next revision and pins; replacement IDs or reset counters cannot
abandon existing records. `intent/<revision-N>/steps/<index-D4>` stores immutable per-chunk
inference/check receipts and a value/ETag commitment chain. The root retains
revision, source snapshot/integrity pin, model configuration, completion counters,
attempt count, latest known failure and pending dispatch. Prior roots live under
`intent/history/<occurrence-D2>`. Final draft content is in the last inference
receipt; checked claims and original references are exposed in the review result.
Nothing is copied into `StoredCharter` or product governance.

Raw source is temporary; materialized buffers are disposed/cleared. Only bounded
findings, permitted minimal excerpts, support explanations and provenance persist.
Repository instructions are inert, no repository tools/builds/tests are executed,
no tools are offered to the model, and no source is sent to public research.
Requests disable stored model completions. Provider errors are redacted.
Secret/instruction-pattern screening is conservative, not a guarantee that every
possible secret or adversarial phrase can be recognized.

Model semantic judgments remain fallible. No numeric accuracy or production
readiness is asserted. An eight-claim candidate may be insufficient for complex
products: the model must report incomplete rather than suppress material intent.
Owner review is still required even when both passes complete.

[Publish and adopt the reviewed charter](publish-adopt-charter.md) through the
separate operator command. Council preview and activation remain separate;
inference never grants either authority.
