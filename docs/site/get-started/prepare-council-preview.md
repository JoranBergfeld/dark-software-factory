# Prepare council preview infrastructure

`dsf onboard decide preview` now reviews and provisions **native preparation
only**. It does not publish the protected preview capability, create an
execution seed, call the evaluation routes, grant activation, or file anything.
`ConfirmedNotReady` is not council readiness or completed onboarding.

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
These are reviewed future evaluation inputs, not evidence of evaluation.

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
| `ConfirmedNotReady` | Exact native effects/receipts confirmed; capability and seed approval remain a separate next increment. |
| `Blocked` | Required current authority/configuration differs or is unavailable; prerequisite owner must reconcile it. |
| `KnownFailed` | Definitive rejection, distinct from uncertain acceptance; no automatic retry. |
| `Canceled` | Cancellation recorded; inspect whether any effect was dispatched. |
| `Unknown` | Lost acknowledgement, incomplete/corrupt/orphaned history, ambiguous native outcome or ownership change; retain resources and request IDs, never replay. |

Exit `0` means only a successful review or confirmed native preparation;
failure states exit `1`, cancellation `130`. `currentReady` is always false.
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

**Remaining integration:** conditional capability/manifest publication,
operator-approved native seed/execution configuration, and CLI consumption
of `/prepare-evaluation`, `/evaluate` and `/evaluation`. Runtime proof APIs
already exist, but native preparation alone cannot authorize or replace them.
Legacy model/telemetry/supplemental verification isolation checks are not
expanded by this increment. Their old `--verify-only` paths do not recognize
the new metadata assignments as legacy consent. The capability handoff must
integrate receipt-bound current proof refresh without a broad admission
switch, implicit old-host upgrade, removing selected sources or resetting
held state. Expired predecessor proof remains a blocker meanwhile.
