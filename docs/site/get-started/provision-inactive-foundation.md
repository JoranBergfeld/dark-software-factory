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

## Receipts, cancellation and inspection

Use the same `status` or `resume` locator as reservation. After foundation dispatch
**both are inspection-only**; they do not repeat ARM requests, complete a partial
deployment, roll back resources, release claims or infer a completed factory.

The owner recovery journal stores bounded records at
`dsf/foundation/<operation-guid>/operations/<0000-index>`. Every direct resource
request, deployment and delegated template resource has prior durable intent:
target, API/input revision, client request correlation and requested disposition.
Deployment resources share their parent request correlation and subsequently
receive provider operation/correlation IDs. Product publication uses conditional
SDK writes with the recorded client request ID.

Receipts distinguish actual creation, rejection and unproven outcomes. Fresh
provider incarnation metadata, creation receipts, principal IDs and Cosmos
resource IDs are used where available; tags and matching names never suffice.
For AppInsights API `2020-02-02`, the native HTTP 200 receipt uses `AppId`
and fresh `CreationDate`, bound to the recorded deployment operation, exact
resource and approved factory workspace. Missing, malformed, older or contradictory
evidence remains unknown; native HTTP 200 is not general adoption permission.
No instrumentation keys or connection strings enter these ownership receipts.
Unsupported or incomplete evidence remains `Unknown`. Initial ARM responses
accepted asynchronously are recorded before read-only polling; no PUT is retried.
Incomplete/paginated deployment evidence stays held for richer recovery rather
than being guessed complete.

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
