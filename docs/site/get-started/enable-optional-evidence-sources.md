# Enable optional evidence sources

`dsf onboard decide supplemental` explicitly selects existing FoundryIQ knowledge
or operator-seeded WebIQ access for a
[proven inactive factory](provision-inactive-foundation.md). Nothing is enabled by
default. An Azure-only factory needs no supplemental input, secret, source host
or data grant.

**Supplemental readiness is not council readiness.** Every selected source must
be checked; a legitimate empty supplemental response is `ReadySupplemental` with
reason `Empty`. Missing, denied, malformed, incomplete, unsupported or changed
access blocks that selection. No source silently disappears.
[Fresh dedicated application telemetry](prove-application-telemetry.md) remains
mandatory even when supplemental results are plentiful. This command does not
run a complete council preview, activate, publish, or reconfigure an approved
selection.

## Exact selection

Create a private, nonsecret JSON selection file. Replace all example identifiers,
image digest and secret version with actual reviewed values:

```json
{
  "schemaVersion": 1,
  "runtimeImage": "ghcr.io/your-owner/dsf-runtime@sha256:<actual-64-hex-digest>",
  "audienceClientId": "77777777-7777-7777-7777-777777777777",
  "sources": [
    {
      "kind": "webiq",
      "query": "Public retail accessibility research",
      "publicQueryApproved": true,
      "secretName": "webiq-api-key",
      "secretVersion": "<actual-32-hex-version>"
    }
  ]
}
```

`publicQueryApproved` is the operator's explicit disclosure review, not an
automated privacy guarantee. Only that exact query is sent. DSF never derives a
public query from private repository, charter, application or telemetry text.
The source does not crawl or follow result links.

For FoundryIQ, replace the source entry, or append this entry to select both:

```json
{
  "kind": "foundryiq",
  "query": "Public retail accessibility research",
  "publicQueryApproved": true,
  "searchResourceId": "/subscriptions/22222222-2222-2222-2222-222222222222/resourcegroups/knowledge/providers/microsoft.search/searchservices/product-search",
  "knowledgeBase": "product-kb",
  "knowledgeBaseETag": "<actual-native-etag>",
  "dedicatedSearchService": true,
  "metadataRoleDefinitionId": "/subscriptions/22222222-2222-2222-2222-222222222222/providers/microsoft.authorization/roledefinitions/88888888-8888-8888-8888-888888888888",
  "knowledgeSources": [
    {
      "name": "product-docs",
      "indexName": "product-index",
      "sourceETag": "<actual-native-etag>",
      "indexETag": "<actual-native-etag>"
    }
  ]
}
```

The operator obtains existing metadata from the Search administrator; preserve
native ETag strings exactly, including embedded quotes. ARM IDs must use the
selected subscription and canonical lowercase spelling.

This bounded profile supports an **existing dedicated Search service**, one
existing extractive-data knowledge base, and one to four existing `searchIndex`
knowledge sources with distinct indexes. The workload checks the complete
knowledge-base, knowledge-source and index inventories before and after retrieval:
unselected data assets block service-scoped data permission. Dedication is an
explicit owner declaration plus inventory checks, not a global product-ownership
lock. Shared model-serving authorization is never source-data consent.

Other knowledge-source kinds, shared services containing unselected data,
protected/permission-filtered index modes, changed ETags and incomplete native
inventories are unsupported here. DSF never creates Search, knowledge bases,
knowledge sources or indexes, changes their networking, or substitutes a
Contributor role to make retrieval work.

## Administrator handoffs

| Actor | Required action |
| --- | --- |
| Image administrator | Supply an accessible digest-pinned DSF runtime image implementing `serve-supplemental`, not a placeholder image |
| Entra administrator | Supply an existing single-tenant API application/audience and operator consent; DSF creates no application or consent |
| Factory administrator | Permit only the displayed new source MI and authenticated host in the original internal factory environment |
| Azure grantor | Separately permit creation of the exact displayed role assignments; deployment or successful inspection is not delegation |
| WebIQ/key custodian | Obtain the WebIQ key independently and seed **the product vault's** `webiq-api-key`; supply its actual version and enabled, unexpired metadata |
| Search administrator | Supply existing KB/source/index names and ETags, dedication review, and the existing narrow metadata role below |
| Network administrator | Establish operator connectivity to private factory ingress and actual source connectivity to its selected private vault/Search endpoint; WebIQ additionally needs outbound HTTPS to `api.microsoft.ai` |

The WebIQ source receives **Key Vault Secrets User only at the exact
`<product-vault-id>/secrets/webiq-api-key` scope**. There is no vault-wide grant,
signing-key read, secret write or automatic owner-key copy. The operator's seeding
permission is separate from the source identity's read permission. This profile
currently supports that source-specific secret name only. Preserve the product
vault's content-type/expiry policies when seeding; do not pass a key as an ordinary
command argument or put it in the selection file.
Azure RBAC covers all versions of this one secret; the workload requests only
the selected version. No version-level RBAC isolation is claimed.

The supplied FoundryIQ metadata role must already exist as a custom role with
exactly these actions, no data actions, and no exclusions:

```text
Microsoft.Search/searchServices/read
Microsoft.Search/searchServices/indexes/read
Microsoft.Search/searchServices/knowledgebases/read
Microsoft.Search/searchServices/knowledgesources/read
```

It is assigned at the selected Search service, alongside **Search Index Data
Reader**, whose sole data action is
`Microsoft.Search/searchServices/indexes/documents/read`. Unsupported native role
definitions require administrator investigation, not guessed permissions or
role-definition creation. Search local authentication must already be disabled;
Search and the product vault must already have private network access configured.

Each selected source gets its own newly created user-assigned MI and host:
`df-<factory-prefix>-webiq` and/or `df-<factory-prefix>-foundryiq`. Unselected hosts
and identities are not created. The sources have **no product App Configuration,
Cosmos, charter, repository-source store, model, GitHub App-key or application
write grants**. Configuration is the exact nonsecret deployment-pinned source
selection; the operator-side workflow alone reads and conditionally updates
product governance.

Effective subscription, inherited and transitive assignments are inspected,
including their current native scopes and role definitions. Broader, unrelated,
preexisting, conditionally ambiguous or recreated grants are not adopted. No
source shares the council, code, telemetry or GitHub authentication identity.

## Interactive and complete explicit commands

On an interactive terminal, `dsf onboard decide supplemental` prompts for missing
factory locators and a selection-file path. Blank path means Azure-only only when
no selection or surviving supplemental history exists. A selected source is
inspected, never silently disabled. Review the complete configuration and supply
its exact fingerprint; blank approval remains read-only.

Redirected input and `--json` never prompt. Complete read-only preview:

```bash
dsf onboard decide supplemental \
  --owner-appconfig-endpoint https://owner.azconfig.io --product widget \
  --tenant 11111111-1111-1111-1111-111111111111 \
  --subscription 22222222-2222-2222-2222-222222222222 \
  --input ./supplemental.json --json
```

Review `supplemental.configuration`, `supplemental.resourceReview.sources`
(exact MI/host targets, grant scopes, role IDs and permission actions) and
`supplemental.approvalFingerprint`, then repeat those **same inputs** with:

```text
--approve <supplemental.approvalFingerprint>
```

The source selection, existing Search native snapshot, runtime image, template,
identity targets, source data scopes, query, audience and foundation bindings
are pinned. Native identities, network configuration, grants and governance are
rechecked before effects and around final proof. Already held preparation cannot
be replaced or retried with another approval.
The resource-review digest is part of the approved configuration, so a changed
target or permission policy cannot silently retain the same approval.

Recheck every selected deployed source without creating anything:

```bash
dsf onboard decide supplemental \
  --owner-appconfig-endpoint https://owner.azconfig.io --product widget \
  --tenant 11111111-1111-1111-1111-111111111111 \
  --subscription 22222222-2222-2222-2222-222222222222 \
  --verify-only --json
```

Omit `--input` and `--approve` for verification. Omit `--verify-only` as well for
read-only status. Status reports historical observations; it is not a new access
or connectivity check. Changing/removing an approved source is outside this
create-once capability, not an implicit fallback to Azure-only.

## Proof, recovery and limits

The actual deployed host uses its explicit user-assigned `ManagedIdentityCredential`.
Native token principal, client, tenant, resource and audience are validated.
Source data-plane calls currently support native v1 `appid`/resource-URI tokens;
no first-party v2 audience GUID mapping is inferred. V2 URI-shaped audiences and
unapproved GUID audiences are rejected, not downgraded to developer credentials.
`POST /proof` returns bounded nonsecret provenance. `POST /evidence` uses the same
authenticated workflow and returns bounded untrusted evidence for a supervised
consumer; content is not persisted by this capability. Both routes admit only
the native approved operator; neither approves governance or publication.
These are source observations, not independent governance/isolation approval:
the operator coordinator must re-read the current manifest, exact selection and
native grants/host bindings around their consumption. The source cannot read
unrelated governance stores or make a previously stored proof current by itself.

FoundryIQ uses GA `2026-04-01` knowledge-base retrieval with semantic intents,
activity and source-scoped document identities. WebIQ uses the published SDK
0.1.8 contract: `POST https://api.microsoft.ai/v3/search/web`, `x-apikey`, and
`{query, contentFormat: "text"}`. Its key is read only from the exact selected
product-vault version before and after the call. `WEBIQ_API_KEY` and other legacy
environment overrides cannot bypass this profile's vault/identity contract.
Existing greenfield source overrides remain available on their original paths.

Persisted references contain source-scoped identity/content digests, not provider
credential-bearing URL parameters, raw source text, query/error echoes or secret
values. Known credential material is rejected. Secret provenance distinguishes
the configured version and observed metadata digest; it never claims that DSF
created the secret or invents a secret-creator field.

Selection files are bounded to 8192 bytes; public-safe ASCII queries to 1000
characters; each source result to 16 complete records and 8192 content characters
per record. Native source payloads are bounded to 256 KiB, secret responses to
16 KiB, and returned proof to 8 KiB. Source calls have a 60-second execution
budget; CLI proof transport has a 75-second deadline. Excess data, continuations
or incomplete activity block rather than truncate into success. Durable
configuration, proofs, operation receipts and the eventual manifest are checked
against App Configuration's 10 KiB **total per-key record**, including metadata.
Proof promotion also requires observations within two minutes of the
coordinator's current clock; historical status is not a freshness assertion.

Manifest selection pins, operation intents, provider creation receipts, proof
references and check revisions are durable, independently conditional records;
they are **not** a multi-key transaction or remote fencing guarantee. Lost
dispatch/receipt responses remain `Unknown`, with original intents retained.
Cancellation stops local progression, not already issued remote effects.
Unconfirmed holds never claim that earlier authority was revoked. Inspect the
same factory on another machine; do not recreate resources, rotate keys,
repair networks, remove approved inputs or treat lease expiry as recovery.

Exit `0` means an Azure-only inspection or a successful supplemental result;
preview/not-ready/error returns `1`, canceled operation `130`. `isActivated`
remains false. A future complete council preview must consume current selected
source references **and separately re-prove mandatory application telemetry**.
