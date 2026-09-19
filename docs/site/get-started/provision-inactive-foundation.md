# Provision an inactive Decide foundation

`dsf onboard decide provision` consumes a **completed remote reservation** and
separate approval of its full plan fingerprint. It creates an isolated factory
base, not completed onboarding. It does not run `dsf new` or `main.bicep`.
Application resources, evidence backends, repository automation and shared owner
infrastructure remain untouched.

## Approve explicitly

Complete [reservation](reserve-factory-identity.md), then use its trusted locator
and full reviewed fingerprint:

```bash
dsf onboard decide provision \
  --owner-appconfig-endpoint https://owner.azconfig.io --product widget \
  --tenant 11111111-1111-1111-1111-111111111111 \
  --subscription 22222222-2222-2222-2222-222222222222 \
  --reviewed-fingerprint <full-reserved-plan-fingerprint> --json
```

Redirected terminals and `--json` require every argument explicitly.
An interactive terminal can prompt for the fingerprint and prints the equivalent
explicit command. Reservation consent alone is insufficient. There is no `--yes`,
force switch, selection-file override, adoption or automatic admission repair.

The CLI verifies remote claims and immutable plan bytes, then rechecks the
current repository, Azure identity, owner admission and reviewed inputs using the
reservation's existing orchestration. Changes block dispatch. The prepared owner
and effective `az`/`gh` identities must remain available.

The operator needs deployment permissions and owner-store data-plane access.
Product App Configuration publication also needs **effective data-plane access**;
control-plane Owner/Contributor permissions alone do not supply it. DSF does not
grant the operator or workloads blanket access, repair networking, or retry writes
to wait for RBAC propagation. A rejected publication leaves inspectable partial
infrastructure.

## Actual footprint

The base uses the reviewed factory resource group and location. Stable resource
names derive from the reserved Factory ID; they are not ownership evidence.

| Created foundation | Deliberate limits |
| --- | --- |
| Product App Configuration | Standard, Entra-only authentication, purge protection; public authenticated operator endpoint plus a factory private endpoint |
| Factory Key Vault | RBAC authorization, purge protection, 90-day retention, public network disabled; no App key copied |
| Factory Cosmos account and product-named database | Local authentication/public network disabled; shared autoscale maximum 1,000 RU/s |
| Cosmos containers | Existing `/id` conventions for `working`, `records`, `lessons`, `charters`; `/product` for `runs`, `learning`; no data-role grants |
| Factory-only Log Analytics and Application Insights | Ingestion/query public access disabled; telemetry connectivity and effective readiness remain pending |
| Isolated VNet, NSG, private endpoints and private DNS | Vault, Cosmos and App Configuration paths only; no shared VNet, DNS, diagnostics or application changes |
| Internal Container Apps hosting environment | No Container Apps or workers; platform-managed hosting resource group is named and recorded before dispatch |
| Four user-assigned identities | Separate `factory:council`, `factory:source-azuremonitor`, `factory:code-intent`, `factory:github-auth`; actual principal/client IDs recorded, no grants |

The Container Apps platform creates its own dedicated
`df-<factory-id-prefix>-hosting-infra` resource group. Its receipt is bound to the
new hosting environment's provider `managedBy` relationship and creation receipt,
not its name or tags. DSF does not mutate that group directly.

Infrastructure incurs charges even while inactive. Endpoint names, private
endpoints, identities and containers do **not** prove effective workload access.
No code-analysis or activation worker is deployed. GitHub authentication, grants,
evidence/network proof, factory telemetry connectivity, models, code analysis,
charter, readiness and activation remain explicitly unmet.

Continue with [factory GitHub authority verification](verify-factory-github-authority.md)
for the separately approved key copy, authentication-only grants and deployed
read-only proof. That step does not activate this foundation.

Once the protected product manifest exists, an authorized operator can also
[record a deliberate council pause](prepare-council-preview.md#deliberately-pause-council-admission)
without ready source/model/telemetry dependencies. It reuses the protected
operator store permissions; it creates no grant, host, repository mutation,
application/tag write or completed-stop claim.

## Product authority and staged handoff

The owner product claim retains the foundation operation ID, progress and
discovery pointer. Its old plan chunks are an immutable admission/recovery
archive, not an editable product manifest.

After the product store is created, publication is staged:

1. Record the product endpoint, expected profile pin and pending handoff.
2. Conditionally create the inactive profile and bounded reviewed-plan chunks.
3. Conditionally create the product manifest, then acknowledge the handoff in the owner.

Product records are unlabelled:

| Key | Meaning |
| --- | --- |
| `dsf/factory/profile` | `onboard-decide-inactive-v1`; no allowed repository mutations |
| `dsf/factory/reviewed-plan/<0000-index>` | Lossless, bounded copies of the admitted plan bytes |
| `dsf/factory/manifest` | Factory identity, reviewed byte digest, profile pin, product authority and inactive publication |

The manifest's reviewed digest must still match the admitted byte revision.
Rehashing edited plan bytes cannot approve them. Missing, corrupt or unreadable
product authority yields `Unknown`, never an owner-archive fallback.
There is no cross-store transaction: interrupted handoffs remain visible and
inactive, including when a product write committed but its response was lost.
No legacy live runtime-index keys are published.

## Recover an interrupted operation

On another machine, install the CLI and authenticate the effective `az`/`gh`
operator identities normally. No original directory, local report or operator
credential export is needed. Use the original trusted owner locator:

```bash
dsf onboard decide status \
  --owner-appconfig-endpoint https://owner.azconfig.io --product widget \
  --tenant 11111111-1111-1111-1111-111111111111 \
  --subscription 22222222-2222-2222-2222-222222222222 --json

dsf onboard decide resume \
  --owner-appconfig-endpoint https://owner.azconfig.io --product widget \
  --tenant 11111111-1111-1111-1111-111111111111 \
  --subscription 22222222-2222-2222-2222-222222222222 --json
```

Interactive `status`/`resume` prompt for missing **nonsecret locator** arguments
and print an equivalent explicit command. Redirected terminals and `--json`
never prompt: supply all four arguments. An authorized different reader can
inspect; mutation credentials must still bind the reviewed Azure principal.
Tokens are acquired for this invocation, never stored in recovery records.
`--input` remains exact reservation-plan repair data, not foundation authority.
There is no force, lease-expiry, new-ID, rollback or cleanup switch.
Missing governing pointers with surviving owner foundation history are
`Unknown`, not pristine reservations. The bounded history read must be complete;
unreadable, oversized or unrecognized history cannot authorize a new operation.

`status` reads the original durable journal and protected product authority;
an inactive foundation is historical provisioning evidence, not current
activation or workload readiness. `resume` reconciles that same immutable
Factory ID and foundation operation, then continues only proven eligible work:

| Durable boundary | Recovery behavior |
| --- | --- |
| Approved root, no dispatched step or surviving operation history | Recheck the current full review and pinned packaged request, win the owner ETag CAS, then continue original provisioning |
| Initial ARM 400/401/403 with durable `Failed` / `NoEffect` | Recheck review and target absence; retry the same step conditionally, retaining the rejected attempt and a distinct request correlation |
| Completed group, but product store not yet created | `Blocked`: no supported current resource-group incarnation; no new child ARM request |
| Original product store incarnation, confirmed publication prefix | Verify original request, native policy, exact key values and original ETags; conditionally create remaining profile/plan/manifest keys |
| Definite no-effect product-key rejection | Retry only the same absent key and original value; retain prior rejection |
| Completed product handoff, base deployment not dispatched | Retain `Blocked` administrator handoff rather than guessing resource-group ownership |
| Acknowledged native deployment, now terminal | Match original request/correlation, protected product revisions and every target's original/current creation evidence; reconcile receipts without another deployment |
| Missing intent, unresolved dispatch, lost publication ETag, changed/recreated resource, newer authority, unavailable original request bytes | Remain `Unknown` or `Blocked`; preserve claims, identity and receipts for administrator reconciliation |

Retries of **proven no-effect** attempts are bounded to eight retained prior
attempts. They do not apply to native deployment `Failed`/`Canceled` outcomes:
accepted work can leave resources. A previously completed step is not rewritten
to adopt changed ownership. Newer CLI assets whose request hash differs from
the admitted request block continuation; a local template cannot approve itself.
Retry admission names the next client request in the owner root before replacing
the step intent. If interrupted between these conditional writes, the old
`NoEffect` receipt does **not** prove the new attempt never dispatched.
The visible pending retry stays unknown, not eligible for another takeover.

The [Resource Groups GET contract](https://learn.microsoft.com/en-us/rest/api/resources/resource-groups/get?view=rest-resources-2021-04-01)
does not guarantee `createdAt`, `systemData`, a resource GUID or generation ETag.
The original HTTP 201/request/date is historical, not proof that today's group
was never deleted/recreated. Recovery therefore cannot automatically resume
new ARM work inside that group. Verified product-key handoff and reconciliation
of already-issued deployment effects do not require pretending otherwise.

Native [deployment operation responses](https://learn.microsoft.com/en-us/rest/api/resources/deployment-operations/list?view=rest-resources-2025-04-01)
may retain original per-target response content. Where an original target
incarnation receipt was lost, recovery requires this native creation evidence
and compares its incarnation/identity with current provider state. A terminal
deployment plus current resource names is insufficient. Missing optional response
content remains `Unknown`. DSF neither enables ARM debug-response capture nor
stores whole provider responses, which can contain secrets.

Before a safe continuation, a fresh owner-root ETag CAS prevents the previous
DSF coordinator from admitting its **next** dispatch. This works only at a
verified no-dispatch or acknowledged-effect boundary: every following dispatch
must first win that CAS. Takeover writes a fresh nonsecret coordinator ID;
it does not assume re-PUTting identical bytes advances the provider's ETag.
A process paused after admission but before sending
can still send; its missing/intent/unknown receipt blocks takeover. Heartbeat
age does not fence a process, ARM or an administrator.

For blocked recovery, retain the trusted locator, Factory ID, operation ID,
stage, root revision and per-step receipts. Give these to the owner-registry
and resource administrators to establish original requests, current incarnations
and any still-running work. Do not delete records, free claims, rename the
factory or invoke a fresh reservation to evade uncertainty. The CLI offers no
administrator declaration that converts missing proof into successful recovery.
Trusted partial records remain available for separately reviewed future cleanup.

## Receipts and cancellation

The owner recovery journal stores bounded records at
`dsf/foundation/<operation-guid>/operations/<0000-index>`. Every direct resource
request, deployment and delegated template resource has prior durable intent:
target, API/input revision, client request correlation and requested disposition.
Deployment resources share their parent request correlation and subsequently
receive provider operation/correlation IDs. Product publication uses conditional
SDK writes with the recorded client request ID.

Receipts distinguish actual creation, definite no-effect rejection, native
failure/cancellation and unproven outcomes. Fresh
provider incarnation metadata, creation receipts, principal IDs and Cosmos
resource IDs are used where available; tags and matching names never suffice.
Cosmos native RIDs use `-` in place of `/`, not Base64URL's replacement for `+`.
Validation checks four-byte database and eight-byte container identifiers while
receipt hashes retain the original provider spelling. Required provider GUIDs
that are absent, null, malformed or empty leave structured `Unknown` results and
the recorded operation snapshot intact; they never authorize reissue.
For AppInsights API `2020-02-02`, the native HTTP 200 receipt uses `AppId`
and fresh `CreationDate`, bound to the recorded deployment operation, exact
resource and approved factory workspace. Missing, malformed, older or contradictory
evidence remains unknown; native HTTP 200 is not general adoption permission.
No instrumentation keys or connection strings enter these ownership receipts.
Unsupported or incomplete evidence remains `Unknown`. Initial ARM responses
accepted asynchronously are recorded before read-only polling; no PUT is retried.
Incomplete/paginated deployment evidence stays held rather than being guessed
complete. Original receipt fields and prior no-effect attempts remain in the
bounded journal across client reconstruction and lost acknowledgments.

Cancellation closes new local dispatch. An accepted ARM operation may continue
remotely. Lost or malformed responses retain in-flight/unknown receipts and the
same operation identity. ARM create-or-update APIs do not provide a general
create-only transaction or remote fencing against unrelated administrators:
absence checks are not such a guarantee, and a returned update is not adoption.
There is no lease-expiry fencing, automatic rollback or name-based deletion.

| Exit | Meaning |
| --- | --- |
| `0` | Verified **Inactive** foundation only |
| `1` | `Partial`, `Blocked`, `Failed` or `Unknown`; inspect stage and per-operation outcomes |
| `2` | Invalid command grammar |
| `130` | Canceled; previous/in-flight effects can remain |

The reservation's `isProvisioned` and `isActivated` remain false.
Read `foundation.state`, `foundation.isBaseProvisioned`, `operations` and
`pendingStages` instead. `hasRegistryEffects` describes this invocation, not all
historical resource effects.

## Runtime and installed assets

The product profile has a pinned SHA-256 revision, exposed as `profilePin`.
`DSF_FACTORY_PROFILE_PIN` is restrictive runtime configuration, **not activation
authority**. The shared runtime workflow denies onboarding work before loading
runs, acquiring sweep leases, composing mutation-capable services or polling
outcomes. HTTP run/gather requests receive 403. Even dry runs are denied in this
foundation-only release; later supervised previews need their own approved gates.

Every admission reads current unlabelled product configuration using the runtime
identity's authenticated Azure SDK connection. The product manifest is the trusted
registration: its factory/product/endpoint binding and pinned inactive profile
must agree, including version, publication policy and empty mutation permissions.
An environment pin can constrain that registration, never replace it. Missing,
malformed, unreadable or inconsistent authority denies work; nothing is cached as
continuing authorization. Equivalent DNS spellings resolve to the same store.

Greenfield compatibility requires the **complete existing product record**
published by `dsf new`: repository, taxonomy, all four source scopes and the
product-keyed threshold, read successfully from that product's authenticated store.
Repository configuration must match when supplied. Empty stores, endpoint names
and absent optional markers are not greenfield registration. Any factory governance
record, configured pin or reserved endpoint hint forbids this legacy fallback;
onboarding never seeds the legacy registration as an escape path. Existing
registered greenfield factories retain their manual filing gates and behavior.

The separate [operator activation review](prepare-council-preview.md#review-operator-activation-authority)
can retain an exact, currently ready approval. This inactive release still
rejects final promotion: approval does not change the manifest/profile,
complete onboarding or enable runtime work. Deliberate pause remains closed.
No new governance-write or repository-mutation grant is given to runtime.

The source host rechecks admission on every gather; periodic ticks and resumed
runs use the same gate. Legacy `dsf-runtime sweep pause|resume|interval|status` also require
admission before accessing their control store. For inactive foundation status,
use `onboard decide status`, not the legacy sweep controls. No grants are added
to make a failed governance read succeed.

Installed packages include `assets/infra/onboard-foundation.json` and the matching
runtime bundle. Neither a checkout nor Bicep is needed on the operator machine.
Missing/corrupt packaged assets fail before provider authentication. Do not
deploy the template directly: that bypasses the reservation, receipt and
manifest-handoff protocol.

The separate [application telemetry capability](prove-application-telemetry.md)
can now prepare the isolated source and prove its real managed-identity/network
path while inactive. It does not relax ordinary run/gather or publication admission.
