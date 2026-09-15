# Preview a dedicated application boundary

`dsf onboard decide plan` combines a **fresh repository assessment** with Azure
discovery and an exact application selection. It is read-only: no reservation,
resource, grant, secret copy, Azure tag, label, issue or PR is created. It does not
onboard, provision, activate, run telemetry queries, or start delivery/operations.
`dsf new` remains the separate greenfield workflow.

Use the installed CLI with existing `gh` and `az` authentication and the
[prepared owner control plane](bootstrap.md). No checkout, new runtime verb,
provisioning template or factory credential is required for planning.

## Interactive selection

```bash
dsf onboard decide plan
```

Choose one listed tenant/subscription explicitly, name the **application
environment**, and enter individual ARM resource IDs. Resources are discovered
across resource groups. A selected site does not select its hosting plan, group,
children or dependencies. Choose dedicated Log Analytics workspaces separately
and specify their Analytics tables. Selecting an Application Insights component
does **not** select its backing workspace.

Enter a genuinely new isolated factory group and its location. Explicitly selected
existing model-serving accounts are reused prerequisites, never application
members or evidence backends; they may be shared. Model deployment/family/access
configuration remains a separate prerequisite. Blank model selection means none,
not silently provisioned or substituted jurors.

The CLI collects all three repository automation declarations, displays the
categorized draft and its selection fingerprint, then asks the accountable owner
to declare dedication and cross-owner exclusivity for that exact selection.
Type `dedicated`/`not-dedicated` and `exclusive`/`not-exclusive`; generic `yes` is
not authority. End-of-input cancels. A blocked/unknown draft is not confirmed.
After declarations, planning rereads remote state; intervening changes invalidate
the declaration rather than silently accepting a wider selection.

The final JSON includes the complete normalized `selection` for explicit replay.
The CLI writes no local selection file.

## Complete explicit / redirected input

`--input FILE` reads a version-1 JSON selection. `--json` disables prompts;
redirected invocations without a complete file fail rather than choose defaults.
Input is a file, not stdin. All fields below are required, including explicit empty
model arrays and `boundaryReview: null` for an unreviewed draft.

Replace these example identities/resources/endpoints with actual selections:

```json
{
  "schemaVersion": 1,
  "repository": "acme/existing-application",
  "ownerAppConfigEndpoint": "https://owner.azconfig.io",
  "ownerKeyVaultUri": "https://owner.vault.azure.net/",
  "productKey": "widget",
  "tenantId": "11111111-1111-1111-1111-111111111111",
  "subscriptionId": "22222222-2222-2222-2222-222222222222",
  "applicationEnvironment": "production",
  "applicationResourceIds": [
    "/subscriptions/22222222-2222-2222-2222-222222222222/resourceGroups/app-a/providers/Microsoft.Web/sites/api",
    "/subscriptions/22222222-2222-2222-2222-222222222222/resourceGroups/app-b/providers/Microsoft.Storage/storageAccounts/data"
  ],
  "evidenceBackends": [
    {
      "resourceId": "/subscriptions/22222222-2222-2222-2222-222222222222/resourceGroups/logs/providers/Microsoft.OperationalInsights/workspaces/application",
      "tables": ["AppRequests"]
    }
  ],
  "factoryResourceGroup": "widget-decide",
  "factoryLocation": "swedencentral",
  "modelResourceIds": [],
  "automationOwner": "acme/platform",
  "automationDeclarations": {
    "ProposalIssue": "Unknown",
    "CharterPullRequest": "Unknown",
    "LabelCreation": "Unknown"
  },
  "boundaryReview": null
}
```

Run a draft; a nonzero exit still supplies the partial/blocked/unknown report:

```bash
dsf onboard decide plan --input selection.json --json > draft-plan.json
```

Resolve repository findings through the
[repository review contract](review-repository-attachment.md). Set each declaration
to `Clear`, `Conflict` or `Unknown` honestly; `Clear` never overrides inaccessible
inventory, opaque automation or a known trigger. Rerun the draft after changes.

After reviewing its current exact selection, replace `boundaryReview` with:

```json
{
  "selectionFingerprint": "<selectionFingerprint from the current draft>",
  "accountableOwner": "acme/platform",
  "dedicated": true,
  "crossOwnerExclusive": true
}
```

Rerun to produce the reviewed plan, then pin any subsequent read-only replay to
that plan's **full** fingerprint:

```bash
dsf onboard decide plan --input selection.json --json > reviewed-plan.json
dsf onboard decide plan --input selection.json \
  --reviewed-fingerprint <fingerprint-from-reviewed-plan> --json > current-plan.json
```

A saved report is not an input manifest or adoption authority. Unknown, duplicate,
missing and invalid JSON fields, numeric declarations, group IDs, overlapping
categories and cross-subscription IDs fail. Owner endpoints must be credential-free
public-cloud HTTPS service roots; they are explicit, not inferred from environment
variables for this command.

Resource-ID case/trailing slashes, input whitespace, resource ordering and repeated
application/model IDs are canonicalized. Duplicate backend entries are ambiguous:
combine their tables explicitly. JSON object ordering does not affect decisions.
Changed identity, selections, declarations, repository facts, claim observations,
permissions or relevant networking invalidate earlier review. Unrelated discovered
resources and unselected tables do not. Fingerprints are versioned SHA-256 stale
input checks, **not signatures or authorization tokens**.

## What discovery establishes

The CLI lists enabled Azure CLI subscriptions and verifies the selected
tenant/subscription against the ARM token's tenant and operator object ID. It does
not log in or change the default Azure CLI account. Resource/group reads, owner
App Configuration reads and ARM requests use the explicitly selected subscription.
The existing owner key is read only for the established App-authenticated GitHub
assessment; no installation token or factory key copy is created.

Resource details, relevant tag signals, workspace identity, networking settings,
selected table plans and ARM permission observations appear separately. ARM
permission collections are paginated with bounded, same-endpoint continuations;
malformed, duplicate, denied or incomplete data stays unknown. Raw provider error
bodies and access tokens are withheld.
Omitted ARM data-action fields remain `null` (unreported), not invented empty
permissions. Legacy DSF tag values are fingerprinted rather than copied into output.

Shared/dedication contradictions, staging/production mixing, existing DSF signals,
selected shared-host boundaries and unsupported backend/table routes block the
plan. Existing groups cannot be adopted as new factory groups. Known Application
Insights links to an unselected workspace block; a selected workspace backing an
unselected component is shared, even if the chosen component has its own metadata
or resource-context access setting. No backing resource is automatically added.

Initial routes are direct Log Analytics workspace queries using selected Analytics
tables: `AppRequests`, `AppExceptions`, `AppTraces`, `AppDependencies`, `AppEvents`,
`AzureDiagnostics`, `AzureActivity`, `ContainerLogV2`, `InsightsMetrics`, `Perf`,
`Heartbeat`. Basic/Auxiliary tables and component-only query routes are unsupported.
No diagnostics, table settings, query filters, tags, firewalls, AMPLS or networking
are repaired to make a route acceptable.

**Limits remain explicit:** same-subscription component-link inspection is not an
exhaustive audit of every diagnostic/ingestion path, other subscription or owner.
Dedication and cross-owner exclusivity require accountable current declarations;
absent tags are not dedication proof. Later evidence checks must independently
establish attribution, confinement, schema, freshness and actual workload access.

Owner inspection reads all labels of `dsf/onboarding/*`, `GITHUB_REPOSITORY` and
`AZURE_APPCONFIG_ENDPOINT` through App Configuration login authentication.
Matching registrations/claims block adoption; unreadable or unfamiliar partial
records do not mean unclaimed. Raw record values are withheld. Existing Azure tags,
including matching or partial legacy DSF tags, are only conflict signals.
An owner-scoped reservation is **not a global lock** across owner authorities.

## Categories, authority and outcomes

The report separates preserved application/repository/evidence assets, newly owned
factory resources, reused owner/model prerequisites, proposed grants, registry
claims, allowed repository additions, costs/omissions and administrator handoffs.
Factory IDs are not allocated by preview. `factory:*` identities/scopes are
**symbolic**, with null principal IDs and mandatory binding rechecks before grants.
Cosmos SQL data-plane roles are distinct from ARM role assignments.

External evidence grants target only the exact selected workspace for the
Azure Monitor source identity. Reused model grants target only selected model
accounts for the council. No application-group or subscription grants are proposed.
No workload receives observation-claim-write or role-delegation authority.

Observed discovery/deployment permissions are not grant authority. After creation,
an eligible grantor must recheck principal/role/scope/condition bindings, inherited
grants and policy. Operator access is not effective workload/network access.
Private connectivity and owner/model/backend access remain administrator tasks;
new networking is confined to the future isolated factory.

`Reviewed` means this boundary review is current within its disclosed limits.
It does **not** clear the separately listed blockers for later grants, models,
connectivity, evidence, charter, preview or activation. Proposals remain unassigned,
proposal-only and create-once; charter PRs need separate operator review and human
merge. No published issue maintenance or delivery handoff is authorized.

| Exit | Meaning |
| --- | --- |
| `0` | Current boundary review; not provisioned, ready or activated |
| `1` | `Partial`, `Blocked`, `Unknown` or `Failed`, with next actor/action |
| `2` | Command-line parsing error, including unsupported `--yes` |
| `130` | Canceled; no remote effects dispatched by planning |

## Contracts for subsequent slices

`Dsf.Core.Onboarding.ApplicationBoundarySelection` and `ApplicationBoundaryPlan`
carry versioned selections, fresh repository/Azure facts, dedication declarations,
the selection/full fingerprints, categories and `OwnerAdmissionSnapshot`.
`ProposedObservationClaim` supplies exact product/repository/resource targets,
unlabelled keys and the owner endpoint. Model prerequisites are not resource claims.
`ProductConfigurationKeys` owns the `dsf/onboarding/` key catalog; resource claim
keys hash canonical ARM IDs.

These are read-only projections, not a claim store, trusted manifest, reservation,
global factory ID or execution approval. A later reservation operation must recheck
fresh authority/state and perform its own conditional, bounded per-key writes and
partial-outcome recovery. This command has no reserve/provision/activate/status
placeholder handlers, and it never falls back to greenfield behavior.
