# Prove required model capabilities

Source acquisition is separate: [acquire resumable source inventory](acquire-source-inventory.md)
uses readonly GitHub authority and does not invoke these models.

`dsf onboard decide models` prepares and proves the complete required model topology
after a [proven inactive foundation](provision-inactive-foundation.md). It creates
only two factory-owned model deployments, exact reviewed grants, and an isolated
model host with native Entra authentication. The three jurors already exist.
Successful deployment or role assignment is **not** successful inference.

The factory remains **Inactive**, registry-owned and proposal-only. These probes
are explicitly nonsensitive capability inputs, **not application evidence,
proposals, council readiness or activation**. No application tags, GitHub writes,
source inventory, charter, preview or activation are performed.

## Reviewed prerequisites

| Owner | Required existing resource or permission |
| --- | --- |
| Model administrator | Existing Azure `AIServices`/`OpenAI` accounts; capacity and supported native versions for two new factory deployments; three existing jurors from the OpenAI/GPT, DeepSeek and xAI/Grok families |
| Serving-account owner | Explicit model-serving-only review, tenant, subscription, location and connectivity; accounts outside the application's application/evidence boundary |
| Azure grantor | The exact custom inference role below, and authority to assign it at each displayed account; no role-definition creation or privilege repair by DSF |
| Factory administrator | Original proven council/code identities and internal Container Apps environment, with unchanged incarnations |
| Image administrator | Accessible digest-pinned image built from the current DSF runtime Dockerfile, containing the real `serve-models` implementation |
| Entra administrator | Existing single-tenant API client ID and operator consent for `api://<client-id>`; no new registration, directory grants or consent |
| Network administrator | Existing operator path to private factory ingress and model-host paths to App Configuration, ARM and the reviewed private model endpoints; no network changes by DSF |

Shared **model-serving** accounts are permitted. Approval never makes those
accounts factory-owned, adds them to application/evidence membership, or grants
access to their projects, files, stored completions or other products' stored data.
Native `publicNetworkAccess` must explicitly be `Disabled`. The account's endpoint
or native `endpoints` map must declare the selected endpoint. This profile supports
only account-name custom subdomains:
`https://<account>.openai.azure.com` or
`https://<account>.services.ai.azure.com`, without paths or trailing slashes.
Other clouds, gateways, custom aliases and different custom-subdomain names fail
closed. All selections must belong to the reserved tenant and subscription.

The proven `factory:council` identity hosts all inference. It gets App Configuration
Data Reader on its own product store and the reviewed custom role at each distinct
serving account. That role must have exactly one permission block:

```json
{
  "actions": [
    "Microsoft.CognitiveServices/accounts/read",
    "Microsoft.CognitiveServices/accounts/deployments/read",
    "Microsoft.Authorization/roleAssignments/read",
    "Microsoft.Authorization/roleDefinitions/read"
  ],
  "notActions": [],
  "dataActions": [
    "Microsoft.CognitiveServices/accounts/OpenAI/deployments/chat/completions/action",
    "Microsoft.CognitiveServices/accounts/OpenAI/deployments/embeddings/action",
    "Microsoft.CognitiveServices/accounts/MaaS/chat/completions/action"
  ],
  "notDataActions": []
}
```

These are explicit inference/metadata operations, not `MaaS/*`, Cognitive Services
User, account-key access or project/data-reader roles. The account-scoped role
permits these inference operations across that account; the DSF host dispatches
only the five reviewed deployments. Review this account-wide inference scope,
even for accounts used by only one juror. The code identity receives no direct
inference grant, signing key or GitHub write token.

The CLI checks exact principal/role/scope/condition/incarnation, role definitions,
grantor authority, deny assignments and inherited/transitive assignment inventory.
Incomplete inventories, unexpected existing authority or unsupported conditions
require grantor reconciliation, not adoption. The runtime rechecks exact model
deployments, accounts, inference assignments and scoped role-definition metadata
before and after inference; inherited working access cannot replace a receipt.
Unsupported scoped role-definition access remains a named blocker, not permission
to add subscription-wide Reader.

## Interactive, explicit and redirected operation

On an interactive terminal, run `dsf onboard decide models`. Missing locator and
topology JSON inputs are prompted. Review the complete native observations,
bindings, grants and fingerprint. Enter that exact fingerprint to prepare; blank
approval leaves a read-only preview. The equivalent explicit command is printed.
Current governance is revalidated after approval and before preparation effects.

For an explicit preview, put the complete nonsecret topology in `models.json`:

```bash
dsf onboard decide models \
  --owner-appconfig-endpoint https://owner.azconfig.io --product widget \
  --tenant 11111111-1111-1111-1111-111111111111 \
  --subscription 22222222-2222-2222-2222-222222222222 \
  --model-topology "$(cat models.json)" --json
```

`--model-topology` accepts **JSON**, not a filename. `--json` and redirected
terminals never prompt. Replace every illustrative identity, model version,
capacity, image digest and deployment name below with reviewed native values.
The names are not an assertion of current regional model availability.

```json
{
  "runtimeImage": "ghcr.io/your-owner/dsf-runtime@sha256:<actual-digest>",
  "audienceClientId": "77777777-7777-7777-7777-777777777777",
  "validForSeconds": 3600,
  "routes": [
    {
      "name": "synthesis", "create": true,
      "accountResourceId": "/subscriptions/22222222-2222-2222-2222-222222222222/resourcegroups/model-serving/providers/microsoft.cognitiveservices/accounts/shared-models",
      "deployment": "df-<factory-prefix>-synthesis",
      "format": "OpenAI", "family": "gpt", "model": "gpt-4.1", "version": "2025-04-14",
      "sku": "GlobalStandard", "capacity": 1, "dimensions": 0,
      "tenantId": "11111111-1111-1111-1111-111111111111", "location": "swedencentral",
      "endpoint": "https://shared-models.services.ai.azure.com",
      "modelServingOnly": true, "connectivityReview": "Reviewed existing private endpoint and DNS; no network changes.",
      "inferenceRoleDefinitionId": "/subscriptions/22222222-2222-2222-2222-222222222222/providers/microsoft.authorization/roledefinitions/aaaaaaaa-1111-2222-3333-444444444444"
    },
    {
      "name": "embedding", "create": true,
      "accountResourceId": "/subscriptions/22222222-2222-2222-2222-222222222222/resourcegroups/model-serving/providers/microsoft.cognitiveservices/accounts/shared-models",
      "deployment": "df-<factory-prefix>-embedding",
      "format": "OpenAI", "family": "text-embedding", "model": "text-embedding-3-small", "version": "1",
      "sku": "GlobalStandard", "capacity": 1, "dimensions": 1536,
      "tenantId": "11111111-1111-1111-1111-111111111111", "location": "swedencentral",
      "endpoint": "https://shared-models.services.ai.azure.com",
      "modelServingOnly": true, "connectivityReview": "Reviewed existing private endpoint and DNS; no network changes.",
      "inferenceRoleDefinitionId": "/subscriptions/22222222-2222-2222-2222-222222222222/providers/microsoft.authorization/roledefinitions/aaaaaaaa-1111-2222-3333-444444444444"
    },
    {
      "name": "juror-1", "create": false,
      "accountResourceId": "/subscriptions/22222222-2222-2222-2222-222222222222/resourcegroups/model-serving/providers/microsoft.cognitiveservices/accounts/shared-models",
      "deployment": "existing-gpt-juror",
      "format": "OpenAI", "family": "gpt", "model": "gpt-4.1", "version": "2025-04-14",
      "sku": "GlobalStandard", "capacity": 1, "dimensions": 0,
      "tenantId": "11111111-1111-1111-1111-111111111111", "location": "swedencentral",
      "endpoint": "https://shared-models.services.ai.azure.com",
      "modelServingOnly": true, "connectivityReview": "Reviewed existing private endpoint and DNS; no network changes.",
      "inferenceRoleDefinitionId": "/subscriptions/22222222-2222-2222-2222-222222222222/providers/microsoft.authorization/roledefinitions/aaaaaaaa-1111-2222-3333-444444444444"
    },
    {
      "name": "juror-2", "create": false,
      "accountResourceId": "/subscriptions/22222222-2222-2222-2222-222222222222/resourcegroups/model-serving/providers/microsoft.cognitiveservices/accounts/shared-models",
      "deployment": "existing-deepseek-juror",
      "format": "DeepSeek", "family": "deepseek", "model": "DeepSeek-V3-0324", "version": "1",
      "sku": "GlobalStandard", "capacity": 1, "dimensions": 0,
      "tenantId": "11111111-1111-1111-1111-111111111111", "location": "swedencentral",
      "endpoint": "https://shared-models.services.ai.azure.com",
      "modelServingOnly": true, "connectivityReview": "Reviewed existing private endpoint and DNS; no network changes.",
      "inferenceRoleDefinitionId": "/subscriptions/22222222-2222-2222-2222-222222222222/providers/microsoft.authorization/roledefinitions/aaaaaaaa-1111-2222-3333-444444444444"
    },
    {
      "name": "juror-3", "create": false,
      "accountResourceId": "/subscriptions/22222222-2222-2222-2222-222222222222/resourcegroups/model-serving/providers/microsoft.cognitiveservices/accounts/shared-models",
      "deployment": "existing-grok-juror",
      "format": "xAI", "family": "grok", "model": "grok-3", "version": "1",
      "sku": "GlobalStandard", "capacity": 1, "dimensions": 0,
      "tenantId": "11111111-1111-1111-1111-111111111111", "location": "swedencentral",
      "endpoint": "https://shared-models.services.ai.azure.com",
      "modelServingOnly": true, "connectivityReview": "Reviewed existing private endpoint and DNS; no network changes.",
      "inferenceRoleDefinitionId": "/subscriptions/22222222-2222-2222-2222-222222222222/providers/microsoft.authorization/roledefinitions/aaaaaaaa-1111-2222-3333-444444444444"
    }
  ]
}
```

`<factory-prefix>` is the first 14 hexadecimal characters of the reserved factory
ID without hyphens. Route order is fixed. Native ARM model format/name/version,
SKU/capacity, ETag and incarnation must agree with the review; three aliases for
one family never pass. Supported SKUs are `Standard`, `GlobalStandard` and
`DataZoneStandard`, with capacity 1..1000. Embedding options are
`text-embedding-3-small`/1536 dimensions and `text-embedding-3-large`/3072.
Lifetime is explicitly 60..86400 seconds. Reviews for routes sharing an account
must agree on endpoint, location, role and connectivity.

Repeat the preview command with
`--approve '<models.approvalFingerprint>'` to prepare. This creates the two absent
deployment children with pinned versions and `NoAutoUpgrade`, exact assignments,
and the packaged `onboard-models.json` host/auth policy. Preparation returns
**Partial**, exit 1, because no workload proof exists yet. Then verify:

```bash
dsf onboard decide models \
  --owner-appconfig-endpoint https://owner.azconfig.io --product widget \
  --tenant 11111111-1111-1111-1111-111111111111 \
  --subscription 22222222-2222-2222-2222-222222222222 \
  --verify-only --json
```

Verification consumes the existing review; omit topology and approval. Missing
packaged preparation assets fail before provider access. Existing preparation
cannot be silently reconfigured, recreated or replaced.

## Actual workloads and consumer seam

`dsf serve-models` forwards to the bundled runtime. The deployed host uses only
the explicit user-assigned `ManagedIdentityCredential`. It checks token
principal/client/tenant/resource, audience and expiry; it does not use operator
credentials or a developer credential chain for inference.

The native transport uses `/openai/v1/chat/completions` and
`/openai/v1/embeddings`, with `model=<deployment>` and Entra scope
`https://ai.azure.com/.default`. ARM reads use API `2024-10-01`; authorization reads
use `2022-04-01`. Synthesis must be nonempty, substantive and fully finished.
Each juror must return a valid `GO:` or `NO-GO:` judgment with substantive rationale.
The response model must match the reviewed native model/version, not a family
prefix. Truncated or malformed responses never become substitute completions.

Embedding responses must cover every input exactly once, with finite, nonzero
vectors of the reviewed dimension. Those vectors actually drive deterministic
complete-link cosine grouping at **0.90**, not a lexical substitute. The grouping
service retains all caller-owned IDs; it neither generates nor persists new
problem identities. Capability inputs demonstrate the route, not embedding
quality or semantic grounding of real application evidence.

| Authenticated caller | Endpoint | Shared contract |
| --- | --- | --- |
| Reviewed operator | `POST /proof` | Complete `ModelCapabilityProof` for all five routes |
| Approved code identity | `POST /synthesis` | `ModelWorkloadRequest` -> `ModelTextResult` |
| Approved code identity | `POST /deduplicate` | `ModelDeduplicationRequest` -> `ModelDeduplicationResult` |
| Approved council identity | `POST /jurors/juror-1`, `juror-2`, `juror-3` | `ModelWorkloadRequest` -> `ModelTextResult` |

Consumers supply `ModelCapabilityReference`: operation ID, configuration revision,
immutable proof reference and proof hash. `FactoryModelsValidation.ReadVerified`
checks current inactive governance, complete receipts, hashes and proof expiry.
The host rechecks authority before returning results. Both proof and consumer
routes use the same asynchronous `IFactoryModelWorkloads` implementation.

These are the implemented model/embedding interfaces for subsequent source
analysis and readiness-preview slices. The existing greenfield lexical clusterer
and persistent Cosmos problem identity resolver are deliberately unchanged; this
slice does not claim a persistent semantic-index migration or a complete council
preview. Later consumers must retain these verified references, not silently
substitute legacy model settings, lexical grouping or a single-model council.

## Durable proof, blockers and recovery

The authoritative product manifest points to `dsf/factory/models`.
Reviewed routes, foundation bindings, grants, effect receipts and immutable proofs
are partitioned under canonical `dsf/factory/model-*` keys, without labels.
Full future authority, receipt and proof envelopes are budgeted against the
**10 KiB total record limit**, including key/label/content-type/service metadata,
before intent or external effects. Model text, embedding vectors and tokens are
not persisted in proof records; only bounded observations, configuration identity,
response-model identity, available request IDs, hashes and group IDs are returned.

Verification conditionally holds capability before calling the actual private
host. Native bindings and governance are checked again before immutable proof
persistence and final promotion. Unconfirmed hold writes, lost responses and failed
final persistence cannot be reported as confirmed revocation or current success.
Prior proofs remain historical. No cross-store/ARM transaction or lease-age fence
is claimed.

| State | Meaning |
| --- | --- |
| Verified | All five hosted routes passed under the exact current review; exit 0, not whole-factory readiness |
| Partial | Read-only preview, completed preparation, or confirmed verification hold; not model proof |
| Blocked | Missing deployment/settings, family mismatch, access/network denial, unsupported target, invalid output or expired proof |
| Failed | Definite rejected preparation or capacity/asset failure; dispatched intent still requires inspection |
| Canceled | Cancellation observed; external operations can continue and current hold may be unconfirmed |
| Unknown | Native effect, conditional write, receipt or final authority cannot be confirmed; no automatic retry |

Proof lifetime starts when probing starts, not when the last model finishes.
A topology taking 300 seconds or the configured shorter lifetime is not promoted
as fresh. The CLI's dedicated proof request allows that aggregate budget plus
30 seconds for transport/authentication/response overhead, at most **330 seconds**.
The deadline covers both sending and receiving the proof; other HTTP clients
retain their existing bounded timeouts. This allowance does not extend proof
freshness or authorize an over-budget host result. A transport timeout is
**Unknown**, not operator cancellation: no result is inferred, no request is
retried, and the held capability cannot authorize consumers. **Canceled** is
reserved for an actually canceled caller token, without claiming current
revocation. Consumer calls reject expired/stale references and changed native
bindings. `status`/`resume` inspect stored authority; they do not repeat inference
or creation. Refresh only through `models --verify-only` after administrator
reconciliation.

Creation ownership requires affirmative HTTP **201** plus native identity,
incarnation and operation receipts. A 201 body still provisioning is observed by
GET, never repeated PUT. Ambiguous 200/202/lost creation responses remain
**Unknown**, even if the resource later exists. This narrow profile does not
auto-adopt it, roll back, relax networking or invent another factory identity.

Provider contracts:
[v1 API and model support](https://learn.microsoft.com/en-us/azure/foundry/openai/api-version-lifecycle),
[Foundry Entra/MaaS permissions](https://learn.microsoft.com/en-us/azure/foundry/foundry-models/how-to/configure-entra-id),
[exact inference operations](https://learn.microsoft.com/en-us/azure/role-based-access-control/permissions/ai-machine-learning#microsoftcognitiveservices),
[scoped role-definition GET](https://learn.microsoft.com/en-us/rest/api/authorization/role-definitions/get?view=rest-authorization-2022-04-01).
