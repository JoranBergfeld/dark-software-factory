# Azure onboarding claims and lifecycle concurrency

Date: 2026-09-15. Research for [#201][ticket]; lifecycle policy remains in
[#193][parent], under [map #189][map].

**Result:** App Configuration documents conditional writes to individual
key-values, not a transaction spanning reservations, manifests, approvals, and
Azure resources. Generic ARM tags do not document conditional claim acquisition.
Some providers document conditional resource updates, but those are not universal
tag leases or protection against a stale, already-running deployment.
The reviewed telemetry APIs do not establish the full required claim guarantee.

## Scope and evidence standard

Preserve the [identity decision][identity], [direct-App topology][topology], and
[readiness decision][readiness]: product App Configuration is manifest authority;
existing owner App Configuration may retain reservations/recovery metadata.
Local copies, matching tags/names, and shared App identity never authorize adoption
or cleanup. No new owner coordination/publication service, runtime tag-write
permission, application mutation, or lifecycle-policy decision follows here.

**Documented** means explicitly described by Microsoft. **Derived** means a safety
consequence of those semantics. **Unsupported** means the reviewed API cannot be
relied upon for the required guarantee, not that every undocumented header was
experimentally proved ineffective. **Unverified** identifies missing provider or
interoperability evidence. **Best-effort** detection is not an acceptable substitute
for the existing no-overwrite contract.

Sources are Microsoft Learn REST contracts/conceptual documentation and one
first-party Azure VM identity article, accessed on the date above. No Azure
control/data-plane calls or live race experiments were performed. REST versions
are explicit below; this is not certification of every provider/version. Direct Azure GitHub
specification retrieval was blocked by organization SAML enforcement; conclusions
do not claim an independent source-code/OpenAPI audit.

## Capability matrix

| Surface | Documented capability | Limit affecting onboarding |
| --- | --- | --- |
| App Configuration data-plane key-value, `1.0` | Conditional create, revision update, and delete for one exact `key` + `label`; failed precondition returns `412`. [A1] | Useful reservation/revision primitive, not multi-key, cross-store, or ARM atomicity. |
| App Configuration reads/replicas | `Sync-Token` provides consistency relative to supplied tokens; geo-replicas synchronize eventually. [A2], [A3] | No documented globally exclusive claim across independent replica writers or automatic failover. |
| App Configuration locks/snapshots | Read-only key locks; immutable snapshot contents. [A4], [A5] | Neither is an expiring owner lease, ARM fencing token, or transactional approval of multiple mutable keys. |
| Generic ARM tags, `2021-04-01` | Replace all tags; merge names/values; selectively delete names or name/value pairs. [T1], [T2] | No documented `If-Match`, `If-None-Match`, claim-pair predicate, or lease. Strict automatic acquisition/release unsupported. |
| DNS zone PATCH, `2018-05-01` | Tags-only request with explicit `If-Match`; does not modify DNS records. Each DNS resource update regenerates its ETag. [P1], [P2] | Positive conditional-update example, not permission to select shared DNS or certification of mixed API paths/recreation. |
| VM PATCH, `2025-04-01` | `If-Match` explicitly prevents overwriting concurrent changes; `tags` accepted. [P3] | Positive resource-update primitive; tags-only side effects, mixed-path ETags, and incarnation races still need provider-specific proof. |
| Log Analytics workspace PATCH/PUT, `2026-03-01` | PATCH accepts tags; `WorkspacePatch` has an `etag` model property, and PUT lists a body `etag`. [P4], [P5] | Neither reference specifies a tag CAS precondition or mismatch semantics. An `etag` property alone proves no such guarantee. **Unverified; block strict automatic claiming.** |
| Application Insights component tags PATCH, `2015-05-01` | Dedicated tags-update operation. [P6] | No conditional header or claim predicate in the reviewed contract. Later/different contracts are not certified. **Block strict automatic claiming.** |
| Container Apps PATCH, `2026-07-01`; Web Apps PATCH, `2026-07-15` | Container Apps documents JSON Merge Patch and tags; neither documents a conditional update header here. [P7], [P8] | Merge is not CAS. Web Apps' reviewed PATCH schema does not expose tags. Neither establishes a safe claim route; other routes remain unverified. |
| ARM deployment, `2025-04-01` | Remote deployment and cancellation; cancellation leaves partial deployment. [D1], [D2] | No App Configuration claim/epoch precondition, rollback transaction, or documented cross-provider stale-writer fence. |

## App Configuration: what the primitive actually protects

The relevant API is the **data plane**, at the store endpoint, not management-plane
updates to the configuration-store resource. Its documented identity and
conditional-write boundary is one exact key and label. [A0], [A1]

| Operation | Documented condition |
| --- | --- |
| Create only if absent | `PUT /kv/{key}` with `If-None-Match: "*"` |
| Update an observed revision | `PUT /kv/{key}` with `If-Match: "<observed-etag>"` |
| Delete an observed revision | Conditional `DELETE /kv/{key}` using its ETag |
| Require existence only | `If-Match: "*"`; **not** protection against a changed value |
| No condition | Unconditional write; reading an ETag does not automatically use it |

These conditions are documented race prevention, not just returned metadata.
The .NET `SetConfigurationSetting` API also defaults `onlyIfUnchanged` to `false`;
having an ETag-bearing object is not by itself a safe update. [A1], [A6]

**Derived constraints for owner reservations and approval references:**

- A stable, agreed key/label namespace can reserve an owner-scoped product key,
  durable repository ID, factory ID binding, or operation record. Labels create
  distinct identities; a unique random factory ID does not enforce either product
  or repository uniqueness. Other owner stores do not participate in that CAS.
- Separate product, repository, and operation keys are separate writes. The
  reviewed REST surface offers no multi-key compare/commit or uniqueness index
  over JSON fields. Putting an invariant in one bounded record can use one CAS;
  splitting it requires a recoverable cooperative protocol and explicit pending
  states, not an assertion that a sequence is atomic. A single key-value is limited
  to **10 KB including key/label and other metadata**. This is a capability boundary,
  not selection of a schema or an unbounded owner-wide registry. [A0], [A1], [A7]
- Owner reservation, product manifest, approval/evidence records, and Azure side
  effects cannot commit together through these APIs. Pre-provisioning identity can
  be retained in the existing owner store, but partial reservation/handoff must
  remain distinguishable from a completed authoritative product registration.
- A CAS can protect a manifest's current revision/reference envelope. It does not
  simultaneously check another mutable approval key, protect referenced evidence
  from deletion, or freeze inputs until a later runtime action. Revision-pinned,
  retained records and revalidation are necessary; a previously reviewed revision
  must not authorize different current inputs. No new publication service is
  implied. [A1], [A5]

**Read and failover limits:** without `Sync-Token`, reads may briefly return cached
data; carry all relevant tokens for the documented consistency guarantee. A
previous writer's missing response also means its returned token may be missing.
Tokens establish consistency relative to known writes, not discovery of all
concurrent writes. Geo-replication permits writes at each replica and is eventually
consistent; the references do not promise globally serialized conditional writes.
Therefore a coordination design must establish one agreed write endpoint and
safe recovery before changing it; treating replica failover as lease transfer is
unsupported. [A2], [A3]

**Lost response:** a create may have succeeded before a retry gets `412`; an update
may have succeeded before its old ETag fails. Neither timeout nor `412` identifies
the winning operator. Reconcile the authoritative operation/factory binding,
intended revision, and current state; do not retry unconditionally or generate a
replacement identity on uncertainty. If later writes/deletion obscure the result,
preserve an unresolved recovery state. This is a consequence of conditional
resource writes, not an exactly-once operation-receipt guarantee. [A1], [A2]

**Locks, history, and authority loss:** key locks are read-only flags with explicit
lock/unlock operations, including conditional forms; no holder identity, renewal
duration, expiry, or ARM-enforced epoch is specified. A client-written `expiresAt`
or generation remains application data. Snapshots guarantee unchanged captured
contents, not atomic mutation of their source keys or approvals; an individual
snapshot is limited to **1 MB** and archived snapshots expire after retention.
Ordinary revision history lasts **7 days** on Free/Developer and **30 days** on
Standard/Premium, not indefinitely. [A4], [A5], [A7]

Store soft delete/recovery is also separate: Standard/Premium retain deleted
stores, but recovery does not restore associated identities, role assignments,
private endpoints, or Event Grid subscriptions. A name can be reused after purge
or retention expiry. A missing/recreated authority, missing key, or reused endpoint
is not evidence that previous external effects vanished or reservations are safe
to discard. No global, never-reused incarnation guarantee for key ETags was
established by the reviewed contracts. [A8], [A1]

## Observation tags: acquisition and release are different problems

The permitted initial pairs remain exactly `(absent, absent)` and
`("false", absent)` for `(dsf-managed, dsf-factory)`; new onboarding rejects all
other pairs. Verified same-factory recovery is separate. Names are
case-insensitive in Azure tag operations, values case-sensitive; unreadable,
inconsistent, unsupported, or capacity-exhausted tags cannot mean unclaimed.
Most resources allow **50 tag pairs**, with documented type-specific lower limits.
Tag write authority is separate from inspection/delegation; generic
`Microsoft.Resources/tags/write` authority does not establish permission to use a
provider's resource-write API. [identity], [T3]

**Generic endpoint:** `PATCH {scope}/providers/Microsoft.Resources/tags/default`
with `Merge` preserves unspecified tag names by its operation semantics, but
overwrites supplied names already present. It cannot require both DSF tags to be
absent, or require `false` plus an absent factory ID. GET/check/Merge/GET is
best-effort race detection, not exclusion. `PUT`, `Replace`, or sending a stale
complete map additionally risks unrelated human tags. Do not invent conditional
headers or JSON Patch `test` support for this API. [T1], [T2], [T4]

**Selective deletion is narrower than claim release.** `Delete` supports name/value
pairs, so it is not equivalent to deleting all tags. But the contract does not
offer an all-or-nothing predicate over the DSF pair. After A's release succeeds
but its response is lost, B can legitimately claim the resource. A's retry deleting
`{dsf-managed: "true", dsf-factory: "A"}` can match B's common `"true"` flag without
matching its factory ID. The API does not promise rejection of the whole request
on that mismatch. Removing only A's factory value still does not atomically restore
the original absent/false pair. An unconditional false-value restoration is also
unsafe. This rules out generic automatic pair release under the preservation
contract. [T1]

**Provider-specific CAS is a real, but bounded, opportunity.** DNS zone tags PATCH
explicitly documents `If-Match`; VM PATCH also documents conditional update.
For an existing resource with unclaimed tags, the relevant token is the
**existing resource's ETag**. `If-None-Match: "*"` on the resource would test whether
the resource exists, not whether the DSF tags are absent. [P1], [P2], [P3]

A provider route can support claim transitions only when its exact contract covers
the whole relevant tag state: read the current resource/tags and token, validate
the allowed pair and identity, and submit only the permitted tag change under
that token. A failed precondition requires rereading and revalidating the claim,
not blindly substituting a fresh ETag. Release additionally needs independent
durable ownership proof and the human-selected released state. Preserve the
**current** unrelated tags, not the entire old baseline. These are conditions
on a future design, not an implemented algorithm or a decision to restore absent
versus false. [P1], [P2], [P3], [identity]

The following remain **unverified per resource type/API version**: whether tag
writes through generic ARM, provider APIs, portal, policy, and application delivery
all advance the token checked by the chosen write; exact tags replacement/removal
semantics; tags-only effects on application configuration; conditional behavior
after resource recreation; and asynchronous write ordering. DNS documents ETag
regeneration on each update, but this research performed no mixed-path race tests.
A provider ETag response, successful PATCH, or generic tag-support listing alone
does not certify these properties. Inability to establish a required property
blocks the route rather than authorizing best-effort overwrite. [P1], [P2], [P3], [T3]

**Consequences across boundaries:** two owners' App Configuration reservations
cannot exclude each other on the same Azure resource. Only an established
resource-side conditional transition can arbitrate that race; a human confirming
an earlier read cannot. Even a valid CAS does not prevent a later independently
authorized unconditional overwrite. Multiple selected resources/backends are
multiple operations: successful earlier claims survive a later conflict/failure,
and compensation is itself a potentially conflicting write. Track observed,
applied, failed, and unknown outcomes distinctly; do not claim rollback or silently
drop a selected backend. [A1], [T1], [P1], [D2]

## Recreation, stale operators, and remote work

An ARM resource ID is a scope/type/name path, not a universal incarnation token.
Provider identities can add evidence: Azure documents an immutable VM unique ID
and its lifecycle behavior, including a different ID for a copied new instance.
That identifier is not a tag-write predicate. Creation timestamps, matching tags,
or current names alone do not establish authorization to alter a replacement.
The reviewed contracts do not supply a universal atomic
`same-incarnation AND same-claim AND current-operation-epoch` test. [R1], [R2]

An observed disappearance, changed incarnation, or uncertain same-path replacement
therefore requires blocked revalidation; never recreate an application resource
to finish tag cleanup. Rechecking an incarnation and then making an unconditional
write leaves another race. Even an ETag-backed route must establish that its
condition is invalidated by recreation; do not assume opaque ETags are permanent,
globally unique incarnation IDs. [R1], [P1], [P3], [identity]

An expired operation record can help **cooperating DSF clients** stop dispatching
new work. It cannot revoke an already accepted Azure write, prevent a paused
operator from dispatching after its last check, or make ARM validate the current
App Configuration generation. Resource CAS prevents a particular stale-version
write where supported; it is not continuing lease enforcement or cancellation of
accepted work. ARM deployment creation documents no such external claim/epoch
precondition. [A4], [P3], [D1]

Remote work has its own lifecycle: preserve available deployment/operation IDs,
scope, API version, reviewed inputs, and polling URLs in permitted recovery
metadata. ARM documents `Azure-AsyncOperation`/`Location`, `Retry-After`, and
terminal status checks; initiating a write does not necessarily grant permission
to poll its status. CLI disconnection or local cancellation is not a remotely
verified terminal outcome. Unknown/unreadable operation status is a blocker, not
proof that a lease timeout made cleanup safe. [D3]

Deployment cancellation is allowed in `Accepted`/`Running`, sets deployment state
to `Canceled`, and leaves the group partially deployed. It is not rollback; the
reference does not specify a cross-provider quiescence barrier for every child
operation. Reconcile relevant operation and resource outcomes before declaring
safe takeover/release. If cessation cannot be established, keep destructive
cleanup, claim reassignment, and conflicting redeployment blocked for
administrator/provider-assisted recovery; no timeout here is a proof. [D2], [D3]

Deployment names are not idempotency/fencing tokens: reuse replaces history and
concurrent same-name deployments can replace one another. Incremental mode is
not a preservation mechanism for resources included in the template: their
properties are reapplied and omitted properties can reset. Do not redeploy selected
application resources to set claims. ARM management locks are broad control-plane
restrictions, not owner leases; they do not govern data-plane writes and may
disrupt application operation. No such new locks or permissions are authorized
by this research. [D4], [D5], [D6]

## Exact constraints handed to the human lifecycle decision

- No atomic all-or-nothing onboarding across keys, stores, approvals, or resources.
  Completion/recovery must account for partial and unknown outcomes.
- No safe automatic generic-tag acquisition or pair release. The reviewed
  Log Analytics/Application Insights contracts do not establish a substitute.
  Because dedicated telemetry is mandatory and its backend must be claimed,
  **automatic end-to-end support for that selection is not established**; do not
  waive its tags, silently omit it, or call an unconditional retry safe.
- Provider conditional updates can prevent specific stale writes, not prove
  ownership, global exclusivity over time, incarnation continuity, or lease fencing.
- Expiry, retry intervals, cancellation acknowledgement, and human approval alone
  do not prove remote writers have stopped. Manual recovery is a blocked-state
  resolution requiring evidence, not a relabeled unsafe automatic write.
- Retained immutable references can support reviewed revisions; finite history,
  archived snapshot expiry, missing authorities, and mutable cross-store records
  can invalidate recovery evidence. They do not authorize replacing approvals.

Retry rules, released tag state, completion, approval lifetime, retention, and
offboarding choices remain in [#193][parent]. No shared-App-key topology change,
new service, live mutation, or implementation was performed.

## Primary sources

- App Configuration: [REST index][A0], [key-values][A1], [consistency][A2],
  [geo-replication][A3], [locks][A4], [snapshots][A5], [.NET setter][A6],
  [limits/history][A7], [soft delete][A8].
- ARM tags: [selective update][T1], [replace][T2], [permissions/limits][T3],
  [CLI merge/delete semantics][T4].
- Conditional providers: [DNS tags PATCH][P1], [DNS ETags][P2], [VM PATCH][P3].
- Telemetry/application providers: [workspace PATCH][P4], [workspace PUT][P5],
  [Application Insights tags][P6], [Container Apps PATCH][P7], [Web Apps PATCH][P8].
- Identity: [resource ID format][R1], [VM unique ID][R2].
- Remote operations: [deployment create/update][D1], [cancel][D2],
  [async tracking][D3], [deployment names][D4], [incremental mode][D5],
  [management locks][D6].

Issue links identify DSF's already-decided constraints, not Azure guarantees.

[A0]: https://learn.microsoft.com/en-us/azure/azure-app-configuration/rest-api
[A1]: https://learn.microsoft.com/en-us/azure/azure-app-configuration/rest-api-key-value
[A2]: https://learn.microsoft.com/en-us/azure/azure-app-configuration/rest-api-consistency
[A3]: https://learn.microsoft.com/en-us/azure/azure-app-configuration/concept-geo-replication
[A4]: https://learn.microsoft.com/en-us/azure/azure-app-configuration/rest-api-locks
[A5]: https://learn.microsoft.com/en-us/azure/azure-app-configuration/concept-snapshots
[A6]: https://learn.microsoft.com/en-us/dotnet/api/azure.data.appconfiguration.configurationclient.setconfigurationsetting?view=azure-dotnet
[A7]: https://learn.microsoft.com/en-us/azure/azure-app-configuration/faq
[A8]: https://learn.microsoft.com/en-us/azure/azure-app-configuration/concept-soft-delete
[T1]: https://learn.microsoft.com/en-us/rest/api/resources/tags/update-at-scope?view=rest-resources-2021-04-01
[T2]: https://learn.microsoft.com/en-us/rest/api/resources/tags/create-or-update-at-scope?view=rest-resources-2021-04-01
[T3]: https://learn.microsoft.com/en-us/azure/azure-resource-manager/management/tag-resources
[T4]: https://learn.microsoft.com/en-us/azure/azure-resource-manager/management/tag-resources-cli
[P1]: https://learn.microsoft.com/en-us/rest/api/dns/zones/update?view=rest-dns-2018-05-01
[P2]: https://learn.microsoft.com/en-us/azure/dns/dns-zones-records#etags
[P3]: https://learn.microsoft.com/en-us/rest/api/compute/virtual-machines/update?view=rest-compute-2025-04-01
[P4]: https://learn.microsoft.com/en-us/rest/api/loganalytics/workspaces/update?view=rest-loganalytics-2026-03-01
[P5]: https://learn.microsoft.com/en-us/rest/api/loganalytics/workspaces/create-or-update?view=rest-loganalytics-2026-03-01
[P6]: https://learn.microsoft.com/en-us/rest/api/application-insights/components/update-tags?view=rest-application-insights-2015-05-01
[P7]: https://learn.microsoft.com/en-us/rest/api/resource-manager/containerapps/container-apps/update?view=rest-resource-manager-containerapps-2026-07-01
[P8]: https://learn.microsoft.com/en-us/rest/api/appservice/web-apps/update?view=rest-appservice-2026-07-15
[R1]: https://learn.microsoft.com/en-us/rest/api/resources/resources/update-by-id?view=rest-resources-2021-04-01
[R2]: https://azure.microsoft.com/en-us/blog/accessing-and-using-azure-vm-unique-id/
[D1]: https://learn.microsoft.com/en-us/rest/api/resources/deployments/create-or-update?view=rest-resources-2025-04-01
[D2]: https://learn.microsoft.com/en-us/rest/api/resources/deployments/cancel?view=rest-resources-2025-04-01
[D3]: https://learn.microsoft.com/en-us/azure/azure-resource-manager/management/async-operations
[D4]: https://learn.microsoft.com/en-us/azure/azure-resource-manager/templates/deploy-cli#azure-deployment-template-name
[D5]: https://learn.microsoft.com/en-us/azure/azure-resource-manager/templates/deployment-modes
[D6]: https://learn.microsoft.com/en-us/azure/azure-resource-manager/management/lock-resources
[ticket]: https://github.com/JoranBergfeld/dark-software-factory/issues/201
[parent]: https://github.com/JoranBergfeld/dark-software-factory/issues/193
[map]: https://github.com/JoranBergfeld/dark-software-factory/issues/189
[identity]: https://github.com/JoranBergfeld/dark-software-factory/issues/194#issuecomment-5676220098
[topology]: https://github.com/JoranBergfeld/dark-software-factory/issues/191#issuecomment-5678928713
[readiness]: https://github.com/JoranBergfeld/dark-software-factory/issues/192#issuecomment-5679718236
