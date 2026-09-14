# Azure discovery and observe-only telemetry access

Research date: **2026-09-14**. Question: [#190](https://github.com/JoranBergfeld/dark-software-factory/issues/190), under [map #189](https://github.com/JoranBergfeld/dark-software-factory/issues/189).

**Outcome: conditionally feasible, not a universal least-privilege attachment recipe.**
Azure can enumerate visible infrastructure and query already-collected, authorized
telemetry without reconfiguring the application. Infrastructure visibility, telemetry
authorization, deployment authority, and role-assignment authority are separate
capabilities. Selected-resource isolation depends on the actual telemetry resource,
record attribution, workspace authorization mode, table plan, effective identity
permissions, and network path.[^arg][^access][^roles][^private-design]

This is documentary research, not a live access audit. No Azure sign-in, subscription
enumeration, credentials inspection, production queries, diagnostics changes, network
changes, or role assignments were performed. Only this findings asset is changed.
The approved boundary is one subscription, explicit resources across multiple resource
groups, one isolated factory per product, and observation only. Existing application
deployments/configuration/diagnostics remain unchanged; new factory infrastructure and
explicitly approved narrow access grants are permitted. These are input constraints
from the map, not policy decisions made here. GitHub authentication, `onboard build`,
`onboard operate`, SRE activation, and remediation are outside this research.

Evidence notation:

- **F**: documented first-party fact; citations name the owning documentation/API.
- **I**: inference or implementation constraint derived from cited facts and the map.
- **U**: unresolved/provider-specific capability; not a promise of support.
- **B**: existing repository behavior verified at base commit
  `b427b762e7f5a9982b64059c995802d8b8d5920c`, not the desired onboarding contract.

## 1. Findings that constrain the decisions

1. **F/I - Resource selection is not telemetry selection.** A resource group is a
   lifecycle/access-management container, not a complete application graph. Resources
   can depend on resources in other groups. A telemetry record may be attributed to
   an Application Insights component or shared collection resource rather than the
   selected application host. Do not silently enlarge membership, grants, or evidence
   scope from proximity, names, tags, or a discovered link.[^arm][^apprequests][^aca]
2. **F - Resource-context is a genuine service-enforced filter, with prerequisites.**
   With resource-based access enabled on the existing workspace, appropriate log-read
   permissions on the telemetry resource can authorize its associated Analytics-plan
   records without a workspace-wide grant. Missing resource attribution, Basic or
   Auxiliary tables, and particular resource types break that path.[^access][^basic]
3. **F/I - A shared workspace is not automatically unusable, but broad workspace
   access is not selected-resource isolation.** Ordinary workspace/table grants expose
   all authorized rows in those tables. A KQL `where` clause is not an authorization
   boundary. Current Azure Monitor also documents granular, server-enforced table/row
   RBAC using conditions; it needs compatible existing data, workspace modes, and no
   overriding grants. This is a conditional alternative, not a universal fix.[^access][^granular]
4. **F - Success and empty results can hide missing access.** Resource queries can
   return HTTP 200 while silently excluding unauthorized workspaces/tables. Protected
   tables can also return successful empty results for unauthorized callers. Neither
   an empty discovery list nor an empty query proves that telemetry does not exist.[^arg][^resource-query][^access]
5. **F/I - Query reachability is independent of RBAC and ingestion.** Private-only
   workspaces need a working runtime-to-Monitor private network/DNS path. ARM-proxied
   log queries cannot use Azure Monitor Private Link. Creating a second AMPLS on shared
   DNS can disrupt existing monitoring, so network remediation is an administrator
   handoff, not an automatic onboarding step.[^private][^private-design]

## 2. Current DSF behavior to avoid mistaking for the contract

| Evidence | Verified behavior and consequence |
| --- | --- |
| **B** [JoranBergfeld/dark-software-factory:dotnet/src/Dsf.Cli/PlannedInstanceDefinition.cs:72-91][base-definition] | `dsf new` defaults monitored resource groups to the new `rg-dsf-<product>` and sets owner authority endpoints. This is not an explicit existing-application resource selection. |
| **B** [JoranBergfeld/dark-software-factory:dotnet/src/Dsf.Cli/AzureProvisioningPlan.cs:78-93][base-plan] | The plan unconditionally appends `DeploySreAgentRequest`. It cannot be assumed to be a council-only onboarding plan. |
| **B** [JoranBergfeld/dark-software-factory:dotnet/src/Dsf.Runtime/AzureMonitorIntegration.cs:17-65][base-query] | The gateway accepts one workspace ID and KQL query, uses `QueryWorkspaceAsync`, looks back 24 hours, disallows SDK partial errors, and requires one result table with string `Reference` and `Summary` columns. It has no selected-resource query argument. |
| **B** [JoranBergfeld/dark-software-factory:dotnet/src/Dsf.Runtime/AzureMonitorIntegration.cs:98-133][base-evidence] | Missing workspace/query settings fail; each evidence row must have nonempty `Reference` and `Summary`. Those output columns alone do not establish which application produced the row. |
| **B** [JoranBergfeld/dark-software-factory:docs/site/get-started/provision-a-factory.md:1-20][base-operator-start] and [112-177][base-operator] | Current .NET provisioning distinguishes factory from application, requires owner bootstrap, and treats external Azure Monitor access as a prerequisite. The guide explicitly warns that `_ResourceId` can be empty in Container Apps custom logs. |
| **B** [JoranBergfeld/dark-software-factory:docs/adr/0021-product-registry-appconfig.md:27-50][base-registry] and [docs/adr/0014-real-only-src-no-offline.md:25-39][base-real] | One product per factory, product/owner App Configuration authorities, and explicit missing-configuration failures inform the boundary. Their Python symbol names are historical; the .NET files above establish current implementation behavior. |

**I:** Do not reuse workspace-query success, the new factory's workspace, inherited
SRE grants, or `Reference`/`Summary` validation as proof of application-scoped evidence.
The telemetry access strategies below are capabilities to evaluate, not implemented
onboarding behavior (see the [gateway][base-query] and [row validation][base-evidence]).[^access]

## 3. Capability and permission matrix

Permissions below are operation requirements or role examples, **not** an instruction
to grant all listed roles. Evaluate operator and runtime separately, at each exact
scope, including inherited assignments, conditions, and deny assignments. A narrow
new assignment cannot subtract a broader existing one.[^scope][^granular][^deny]

| Capability / actor | Required authority or documented example | What success does not prove |
| --- | --- | --- |
| **F** Authenticate operator using Azure CLI | An authenticated CLI context; `az account list --refresh` obtains current visible subscriptions; default listing shows Enabled subscriptions in the current cloud.[^account] | No proof of resource reads, log reads, provisioning, or delegation. ARM authorizes each operation.[^arm] |
| **F** Discover selected subscription/groups/resources: operator | Relevant ARM read operations; `Microsoft.Resources/subscriptions/resourceGroups/read`, `Microsoft.Resources/subscriptions/resources/read`, and provider reads as applicable. ARG returns only resources with read access.[^management-actions][^arg] | Completeness of the subscription, all child/data-plane objects, or business membership. A successful listing can be partial.[^arg] |
| **F** Inspect telemetry routing: operator or separately authorized inspector | Resource read plus operations such as `Microsoft.Insights/DiagnosticSettings/Read`, `DiagnosticSettingsCategories/Read`, `DataCollectionRuleAssociations/Read`, and `DataCollectionRules/Read` at their relevant scopes.[^monitor-actions] | A configured destination is not proof of ingestion, accessible rows, freshness, or runtime authorization.[^diagnostics][^resource-query] |
| **F** Inspect workspace metadata | `Microsoft.OperationalInsights/workspaces/read`; table configuration requires its applicable read permission. A metadata-only custom role is distinct from a generic Reader assignment.[^access][^tables] | Workspace metadata read alone does not give all table data. Conversely, ordinary `*/read` on a workspace can grant broad log reads.[^access] |
| **F** Read existing resource-scoped Analytics logs: runtime | Normally `Microsoft.Insights/logs/*/read`, or legacy table-specific log-read actions, on the telemetry resource; ordinary Reader includes the wildcard read capability. Existing workspace must permit resource authorization. Protected/DataActionsOnly exceptions apply.[^access] | Not all resource-specific custom readers include log-read. No proof that all workspaces/tables are visible or that one resource represents only one product.[^resource-query][^aca] |
| **F** Read workspace-scoped logs: runtime | Query action `Microsoft.OperationalInsights/workspaces/query/read` plus authorized table data. Legacy table/all-table read actions and the newer DataActions/conditions model are distinct paths.[^access][^granular] | A full Log Analytics Reader or unconditioned Data Reader grant is not application isolation in a shared workspace.[^access][^granular] |
| **F** Read platform metrics: runtime, if separately supported | Target resource read plus `Microsoft.Insights/MetricDefinitions/Read` and `Microsoft.Insights/Metrics/Read`; Monitoring Reader is a broader built-in example.[^monitor-actions][^monitor-roles] | Metrics do not imply application logs, traces, workspace exports, or per-application isolation inside a shared resource.[^diagnostics] |
| **F** Create isolated factory group/infrastructure: operator or deployment principal | Creating a group requires `Microsoft.Resources/subscriptions/resourceGroups/write` at the applicable parent scope. Deployment requires resource writes plus `Microsoft.Resources/deployments/*`; provider registration may separately need `/register/action`.[^management-actions][^what-if][^providers] | Contributor does not authorize Azure RBAC role assignment. New infrastructure also remains subject to policy/provider constraints.[^roles][^providers][^mi-faq] |
| **F** Create/attach factory managed identity: deployment principal | System-assigned: write on its factory host. User-assigned: authority to manage the identity, write on the factory host, and identity assign permission; Managed Identity Contributor/Operator cover different operations.[^mi-faq] | Creating/attaching an identity does not authorize that identity to read the application's telemetry.[^mi] |
| **F** Grant runtime a role: authorized grantor | `Microsoft.Authorization/roleAssignments/write` at each target scope, constrained by any delegation condition. Owner/RBAC Administrator/User Access Administrator are examples, not prerequisites at the entire subscription.[^assign][^roles][^rbac-errors] | Ability to grant one role/principal/scope is not authority to grant every requested binding. App-owner approval alone does not confer Azure authority.[^rbac-errors] |
| **F** Define custom role or conditional assignment: administrator | Custom definition needs `roleDefinitions/write` on assignable scopes; granular conditions require applicable assignment write/delete authority on the workspace.[^granular] | RBAC Administrator's assignment rights do not themselves include arbitrary role-definition creation.[^roles] |
| **F/I** Reach telemetry endpoint: runtime | Correct query endpoint, DNS, route, firewall rules and, where required, existing approved Private Link connectivity, independently of identity authorization.[^private][^private-design] | A role assignment, working application, healthy ingestion, or successful query from the operator's laptop does not prove factory runtime connectivity. |

**F/I - Provisioning preflight is not a guarantee.** ARM what-if predicts changes
without applying them; normal Provider validation includes permission checks.
`ProviderNoRbac` checks read rather than full deployment permissions, and Template
validation skips provider/permission preflight. Expansion can short-circuit or mark
resources Ignore. None is a telemetry-readiness test; a reduced-validation success
must not be presented as authority to deploy or assign roles.[^what-if]

## 4. Discovery: one subscription, multiple explicit resource groups

The following are **documented examples for a future implementation**, not commands
executed during this research. `{S}` is one selected subscription ID; `{R}` is a
returned full ARM resource ID.
The Boundary column includes **I** constraints derived from the documented APIs and
the map's explicit-selection rule.

| Purpose | CLI / read API capability | Boundary |
| --- | --- | --- |
| **F** Subscription choices | `az account list --refresh`; `az account show --subscription "{S}"`. ARM `GET /subscriptions?api-version=2022-12-01`.[^account][^subscriptions] | `--all` includes non-Enabled subscriptions/all clouds; it is not an instruction to operate on them. Retain tenant/cloud as well as subscription identity. |
| **F** Resource groups | `az group list --subscription "{S}"`; `GET /subscriptions/{S}/resourcegroups?api-version=2021-04-01`.[^assign][^groups] | Results reflect caller visibility, not a complete tenant/application inventory.[^arg] |
| **F** Resources in each chosen group | Repeat `az resource list --subscription "{S}" --resource-group "{group}"`. ARM list-by-group or subscription resources list, API `2021-04-01`.[^resource-cli][^resources][^resources-group] | Multiple group selections are a union of candidate lists, not approval of every resource in those groups. |
| **F** Direct detail/revalidation | `az resource show --ids "{R}" --api-version "{supported-version}"`; provider-specific GET/list APIs.[^resource-cli][^providers] | Choose a version supported by that provider/type; the generic resources-list API version is not a universal provider API version. |
| **F** Efficient indexed candidates | `az graph query --subscriptions "{S}" --first 1000 -q "Resources \| project id, name, type, resourceGroup, subscriptionId, location \| order by id asc"`.[^graph-cli][^arg-pages] | `az graph query` defaults to **all accessible subscriptions**, not merely the CLI's active subscription. Pass the explicit one-element subscription set. |
| **F** Child/extension detail | Full nested IDs and provider-specific APIs, e.g. `GET .../Microsoft.Web/sites/{name}/slots?api-version=2024-11-01`; diagnostics and DCR associations use extension-resource paths.[^slots][^diagnostic-list][^dcra] | Parent and child are different selection candidates; data-plane objects are not interchangeable with ARM resources. |

### Paging and partial visibility

**F:** ARM subscription, resource-group, and resource list response contracts expose
`nextLink`. A REST/SDK implementation must consume every page, not just `value` from
the first response. `az resource list` itself does not expose a manual continuation
flag in its command contract; do not substitute ARG's `--skip-token` for ARM paging.
Aborted, forbidden, or throttled page retrieval is incomplete discovery, not an empty
selection.[^subscriptions][^groups][^resources][^resource-cli]

**F:** ARG has a maximum page size of 1,000. Use the response continuation token
where available, check truncation/count information, and retain a scalar ID in
projected output. `limit`/`take`/`sample` and all-dynamic/null projections can prevent
continuation; sorting makes repeated pages more predictable. Ordering/deduplicating
by ID does not turn an evolving inventory into a snapshot.[^arg-pages][^arg]

**F:** ARG can return only authorized subscriptions/resources without warning that
visibility is partial; no authorized subscription can instead yield 403.
`--allow-partial-scopes` controls server processing of subscription scopes, not an
access-completeness proof. An operator with access only to individual resources may
need known IDs and direct GETs instead of a browsable, complete group hierarchy.[^arg][^graph-cli]

**F/I:** ARG is eventually consistent, only indexes supported resource types, may
use a provider API version that omits a desired property, and scrubs PII. An absent
row/property is not proof the resource/property does not exist. Use indexed results
for candidates and provider reads for selected details. Neither the generic list
contract nor ARG establishes recursive discovery of every service's nested or
data-plane objects; a provider-specific detail gap must remain visible.[^arg][^arg-types][^providers][^slots]

### Identity and application membership

**F/I:** Persist the returned full ARM ID, not a display name or group/name pair;
retain provider type, subscription, tenant/cloud, and discovery time as separate
context. Handle nested type/name segments and extension providers rather than
assuming a fixed segment count. Azure names may be returned with different casing;
do not treat casing changes as different ordinary ARM resources. This does not
authorize lowercasing arbitrary case-sensitive data-plane identifiers.[^scope][^names]

**F:** ARM IDs are stable addresses while the resource stays at that scope, not
immutable business identities. A resource-group/subscription move changes the ID.
Deletion/recreation can reuse a resource location/name; Microsoft's diagnostics
warning explicitly describes settings applying to a recreated resource.[^move][^diagnostics]

**I:** Names, tags, shared network links, `managedBy`, deployment relationships, and
group membership are hints, not proof of application membership or a complete runtime
dependency graph. The operator's explicit selection remains necessary; discovered
telemetry resources and broader permission scopes need separate review. Direct GET
can prove that a caller currently sees an ID, not that all product dependencies have
been found.[^arm][^resources][^arg]

## 5. Discover existing telemetry without changing collection

**F/I:** Treat discovery as a provenance chain, with a separate status for each
read. Do not create diagnostics, agents, instrumentation, transforms, exports,
tables, or new ingestion routes to make the chain succeed.[^diagnostics][^dcr][^appinsights]

| Existing evidence route | What metadata can establish | What remains unproved |
| --- | --- | --- |
| **F** Resource diagnostic settings | `az monitor diagnostic-settings categories list --resource "{R}"` lists supported categories; diagnostic-settings List returns active settings, enabled categories/groups, workspace/storage/Event Hubs/partner destinations. A resource can have up to five settings.[^diagnostic-categories][^diagnostic-list][^diagnostics] | Supported category is not enabled collection. Enabled collection is not usable or current data. No setting at a parent does not settle child settings or another collection method. |
| **F** Azure Monitor Agent / DCR | List associations at `{R}/providers/Microsoft.Insights/dataCollectionRuleAssociations` using API `2024-03-11`; GET referenced DCRs and inspect streams, data flows, transformations and `destinations.logAnalytics` workspace IDs.[^dcra][^dcr] | Association existence does not prove a running collector, matching events, ingestion, or query permission. Direct-ingestion/workspace-transform DCRs differ from AMA DCRs; not all routes are resource associations. |
| **F** Workspace-based Application Insights | The component's `WorkspaceResourceId` identifies its Log Analytics destination. Current ARM component schema `2020-02-02` includes it. Classic Application Insights is retired; classic query syntax still exists for compatibility.[^component-schema][^appinsights] | Selecting a web app does not automatically identify/authorize its component. One component can represent multiple application roles; role-name filters are not resource-level authorization.[^apprequests][^access] |
| **F** Container Apps logging | Existing environment logging can collect system/application logs for all apps in that environment. Console/system schemas expose app/environment names; HTTP logs are configured at the managed environment.[^aca] | Environment selection or name-based KQL does not prove only this product's rows are authorized. `_ResourceId` attribution must be established for the actual route/table, not assumed from a sample query.[^columns][^access] |
| **F** Workspace inventory/details | `az monitor log-analytics workspace list/show` or provider GET can identify workspaces, ARM ID, `customerId`, retention, query/ingestion public-network flags, Private Link associations, and access features.[^workspace-cli][^workspace][^workspace-new] | A visible workspace is not necessarily related to the product, and an invisible workspace may still hold its logs. Absence from a list is not a negative telemetry finding. |
| **F** Platform metrics / Activity Log | Platform metrics and the Activity Log are collected without diagnostic settings; Monitor supports metric-definition/value reads and filtered activity-log queries. Activity Log is control-plane history, normally retained 90 days.[^diagnostics][^metrics][^activity] | Platform metrics are not automatically workspace `AzureMetrics`; Activity Log is not application requests/traces. Neither is an automatic substitute for a council's required evidence. |

**F/I - Distinct IDs:** A workspace ARM ID scopes management/RBAC. The Logs workspace
query URL uses its GUID (`customerId`), not the ARM ID. The `TenantId` column in Log
Analytics records is the **workspace ID**, not the Microsoft Entra tenant ID.
Application Insights component IDs, app IDs and application-host IDs are likewise
not interchangeable. Preserve explicit mappings rather than guessing from names.[^workspace][^request][^columns][^component-schema]

**F/I - Destinations can escape the selected footprint:** Diagnostic destinations
can be in another subscription, and a single resource can send to multiple workspaces.
Automatic resource-context queries can search multiple workspaces. A one-subscription
application selector alone does not prove a one-subscription telemetry backend.
Record the discovered destination and flag the map's out-of-scope boundary; do not
follow it or expand permissions automatically.[^diagnostics][^resource-query]

**F/I - Do not retrieve keys to discover evidence:** Workspace query authorization
does not need ingestion shared keys or connection-string secrets. Generic Reader
also does not permit all App Service configuration/secret reads or log streaming.
If determining a link would require those sensitive operations, use an operator
supplied, nonsecret component/workspace ID with provenance rather than escalating
or dumping configuration.[^access][^rbac-errors]

**F/I - Metadata is only evidence of configuration:** Tables may be created on first
ingestion; retention, sampling, transformations, quota stops, and ingestion latency
affect visible data. An authorized, bounded query under the runtime identity can
establish that particular usable rows exist at that time. This research did not
perform such a query and cannot certify any actual application's evidence.[^diagnostics][^workspace][^dcr][^apprequests][^latency]

## 6. What actually enforces selected-resource access?

### Workspace mode and query mode are different

| Existing configuration / access path | Documented enforcement | Observe-only implication |
| --- | --- | --- |
| **F** `Use resource or workspace permissions`, resource-context query | Resource permissions authorize associated records; workspace permissions are ignored for this path. Normally uses `Microsoft.Insights/logs/*/read` or a compatible read role.[^access] | Candidate for resource-only grants, provided attribution, table plan, protection mode, and actual identity all fit. |
| **F** `Require workspace permissions`, resource-context query | Resource-context still filters records to the resource, but the caller must have workspace/table permissions.[^access] | A full workspace grant is broader authority even if DSF happens to call only the resource URL. Do not present query scope as the credential's maximum reach. |
| **F** Workspace-context query, ordinary unrestricted workspace/table grant | All rows in authorized tables are available, including unrelated resources sharing those tables.[^access] | A selected-resource `where` or saved function does not narrow the role assignment. A dedicated workspace's ownership can help, but exclusivity is not proved merely by its name/location. |
| **F** Workspace-context query, granular RBAC | `workspaces/query/read` permits running queries/metadata; `workspaces/tables/data/read` grants data, optionally restricted by server-evaluated table/row conditions. Without a condition it grants all data in scope.[^granular] | Conditional shared-workspace access is a documented capability, not inherently forbidden. Actual conditions and existing schemas require verification and administrator approval. |
| **F** Granular RBAC used through resource-context | Microsoft's FAQ requires **all relevant workspaces** to use `Require workspace permissions` and have ABAC configured. With resource-or-workspace access, resource read permission bypasses workspace ABAC on this path.[^granular] | Do not combine resource Reader and workspace ABAC and assume the stricter assignment wins. Changing existing workspace modes is outside this task's allowed changes. |
| **F/U** Protected tables / DataActionsOnly | Access to protected tables needs explicit data-action/protection conditions; resource-centric data action is `Microsoft.Insights/logs/data/read`. DataActionsOnly removes implicit log access from control-plane read roles.[^access][^protected] | An ordinary Reader recipe is insufficient. Protected tables are preview. Read existing state; do not enable/disable protection or authorization modes during onboarding. Wire-schema ambiguity is recorded below. |

The existing workspace flag is
`properties.features.enableLogAccessUsingOnlyResourcePermissions`: true permits
resource-based authorization; false or an absent flag is documented as requiring
workspace permissions. A failed metadata read is **unknown**, not false/default.
Do not alter this flag on an existing shared workspace.[^access]

**F/U:** Protected-table preview also excludes cross-workspace `app()`/`workspace()`
queries. Microsoft's guidance warns that a custom DataAction role without an
explicit `protectionLevel` condition might grant protected data access. Consequently,
neither a role name nor an omitted condition demonstrates a safe restriction.
Granular RBAC's published cloud-availability list names public cloud, Azure Commercial
(GCC), and Azure China; other cloud combinations are not established here.[^access][^granular]

### Granular RBAC is useful but not a universal escape hatch

**F:** Conditions can restrict table names and row column values. Current documentation
supports string comparisons, not arbitrary KQL predicates; its `StringLike` semantics
are token `has`, not SQL `LIKE`. It restricts condition values to alphanumeric
characters plus `@`, `.`, `-`. Normal role-assignment/condition-size limits also
apply. Conditions follow tables into functions but do not protect successfully
exported/copied data at its destination.[^granular]

**U:** Do not claim that an arbitrary `_ResourceId` allowlist can be translated
directly into these conditions: ARM IDs contain `/`, outside that documented value
character set. An existing stable application-key column might support a condition,
but attribution, uniqueness, missing values and all queried tables remain to be
verified. The docs suggest transformations when data is unsuitable; adding those
would change collection and is excluded here.[^granular][^columns]

**F/I:** RBAC is additive. A workspace Reader, Log Analytics Reader, inherited
`*/read`, or another unrestricted data grant can negate intended granular
restrictions. `NotActions` is not a deny across other assignments. Resource-scoped
roles also inherit down the scope hierarchy; a parent/shared resource grant can
authorize descendants beyond the reviewed leaf selection. Inspect the effective
identity, not just the grants onboarding proposes.[^access][^granular][^scope]

**F/I:** Resource authorization partitions records by the Azure resource association,
not by business meaning inside the record. A shared Application Insights component's
`AppRoleName`, a Container Apps environment's app name, or a tenant/customer field
may need a finer boundary. Neither resource context nor row RBAC redacts unrelated
content embedded inside an otherwise authorized row. Free text/URLs may contain
sensitive values; choosing evidence projection/redaction remains a readiness
decision, not something resource discovery proves.[^columns][^apprequests][^aca][^granular]

## 7. Query contract and honest evidence states

**F - Supported query surfaces:** Workspace Logs REST uses
`https://api.loganalytics.azure.com/v1/workspaces/{workspace-guid}/query`;
resource Logs REST uses
`https://api.loganalytics.azure.com/v1{full-arm-resource-id}/query`.
Resource queries can consolidate logs from multiple workspaces and return the same
table-shaped response contract. The SDK documents `QueryResourceAsync`, but current
DSF's gateway calls the workspace method; changing that is future implementation,
not a configuration-only guarantee.[^request][^resource-query][^sdk-resource][base-query]

**F - Resource-context restrictions:** Records need the relevant resource association
(`_ResourceId`). Resource-scoped queries do not support `app()` or `workspace()`
expressions. Microsoft documents regional fan-out warnings at five workspace regions
and blocking at twenty; resource scope is not unlimited aggregation.[^access][^query-scope]

**F - Table-plan restrictions:** Basic and Auxiliary tables only support workspace
scope. Their documented REST query path is `/search`, with `timespan` in the URL;
they restrict joins/functions/cross-resource queries and charge by data scanned.
Basic direct queries cover up to the past 30 days; older data needs search jobs.
Search jobs create tables and need write permissions, so they are not an
observe-only fallback against existing telemetry infrastructure. Granular RBAC
compatibility for a specific Basic/Auxiliary query needs separate confirmation;
this report does not promise it.[^basic][^access][^granular]

**F - API success is insufficient:** Inspect both HTTP status and body errors.
Nonfatal failures can return `200` plus `error.code = PartialError`. Separately,
resource queries can silently filter inaccessible workspaces/tables without such an
error. `Prefer: include-permissions=true` requests details of resource/data-source
permissions and denied tables; it is not a certificate that all telemetry sources
or all application dependencies were discovered.[^response][^resource-query]

**F - Empty results are ambiguous:** Resource queries may return empty 200 or a
4xx syntax error when there are no logs **or** no access to the workspace holding
them. Protected-table denial returns empty success and leaves schema visible.
Therefore neither table/schema discovery nor an empty query proves telemetry
absence, authorized completeness, or application health.[^resource-query][^access]

**F/I - Bounded queries, not inventory pagination:** The Logs response contract
provides tables/errors, not ARM/ARG continuation. Published per-query limits include
500,000 rows, approximately 100 MiB returned data, and ten minutes execution; user
throttling also applies. Time/partition splitting, aggregation and duplicate
handling need an explicit contract, not silent `take` truncation. Current DSF's
partial-error rejection does not resolve permission-filtered successful responses.[^response][^limits][base-query]

**I - Inputs needed before declaring evidence readiness:** These are candidate
verification obligations for #192, not a decided manifest or readiness policy:

- Identity/tenant/cloud, selected application IDs, separately approved telemetry IDs,
  allowed destination workspaces, and evidence-to-application provenance.[^scope][^resource-query]
- Query mode/endpoint/API, tables/plans, existing access/protection mode, effective
  grants and conditions, time window, and the intended runtime network path.[^access][^basic][^private-design]
- Approved query and output projection; stable record references, explicit row
  attribution, freshness and sampling semantics; exactly one table with nonempty
  string `Reference`/`Summary` for the current adapter.[^columns][^apprequests][base-evidence]
- Distinct results for visible metadata, query authorized, partial access, data
  observed, empty/unknown, unsupported schema, and unreachable endpoint. A later
  controlled check must use the **runtime identity**, not merely the operator's
  often broader credentials.[^resource-query][^protected][^identity-practice]

## 8. Managed identity, reachability, and handoffs

**F/I - Separate principals:** A managed identity obtains Entra tokens without a
stored human credential; attaching it to the factory host and authorizing it on
target telemetry are separate operations. System-assigned lifetime follows the
factory host; user-assigned lifetime is independent and it can be attached to
multiple hosts. Any code able to use a host's managed identity shares that identity's
access. Reusing an application/other factory's identity is not isolation merely
because its display name looks suitable.[^mi][^mi-faq]

**F/I - Explicit runtime authentication:** Microsoft's .NET guidance recommends
deterministic production credentials rather than an unconstrained
`DefaultAzureCredential` chain that might select a human CLI identity. Identify the
approved managed identity unambiguously. ARM and Logs use different audiences;
current Logs documentation retains `https://api.loganalytics.io` as the token resource
while moving the query hostname to `api.loganalytics.azure.com`. Use the supported
SDK/cloud configuration; do not infer a token audience solely from a new hostname,
or reuse an ARM token by assumption.[^identity-practice][^mi-faq][^api-auth][^metrics]

**F - Role assignment details:** Managed-identity grants target the service-principal
object/principal ID, not its client/application ID. CLI supports
`--assignee-object-id` to skip directory lookups and
`--assignee-principal-type ServicePrincipal` for newly created identities.
Neither option bypasses authorization. Directory-read failure is not a reason to
request directory-wide privileges when the known object ID suffices.[^assign][^rbac-errors]

**F/I - Propagation is not completion:** ARM role changes can take up to ten minutes;
managed-identity membership/token caches can cause longer delays, especially through
groups. Recreated identities have new principal IDs. Report authorization pending,
bounded retry, or administrator action rather than creating progressively broader
grants or treating role-creation success as evidence readiness.[^rbac-errors][^mi-faq]

**F/I - Private networking:** A private application endpoint does not itself imply a
private telemetry query endpoint, and private ingestion does not imply private
queries: workspace public query/ingestion flags are separate. For private-only
queries, the factory needs usable DNS and routing to the relevant AMPLS private
endpoint, including all required workspace access. Identity permissions still
apply. Private Link network scope is not a row-level application boundary.[^workspace][^private][^private-design]

**F/I - No automatic networking fix:** Monitor log queries use shared endpoints;
adding an AMPLS/private DNS configuration can affect other workspaces and connected
networks. ARM-proxied queries cannot use Monitor Private Link and require public
query access. Do not flip public access, add shared DNS overrides, attach existing
workspaces to a new AMPLS, or change application networking as a fallback. Hand off
an exact endpoint/workspace/network prerequisite to its administrator; only an
approved design confined to newly owned factory infrastructure fits the map.[^private][^private-design]

| Observed condition in a future preflight | Explicit result / handoff, not automatic remediation |
| --- | --- |
| **I** Sign-in/tenant/subscription invalid; 401 | Operator reauthentication or correct context; do not select another subscription automatically.[^account][^api-auth] |
| **I** Resource/list/detail 403, missing page or missing provider detail | Visibility incomplete; name the operation/ID and request specific access or operator-supplied ID. Do not claim absence.[^arg][^resources] |
| **I** Cannot create factory resources | Deployment administrator handles exact denied action, policy, provider registration or quota. Do not deploy into an existing application's group to work around it.[^what-if][^providers][^mi-faq] |
| **I** Role assignment/condition denied | Grantor receives runtime principal ID, exact target scopes, role-definition IDs/actions, conditions, and intended evidence boundary. Retain pending authorization; do not request subscription Owner by default.[^assign][^rbac-errors] |
| **I** Workspace mode, attribution or plan incompatible | Evidence source unsupported under current configuration; telemetry administrator explains existing compatible alternatives. No diagnostics/mode/transform changes.[^access][^basic][^granular] |
| **I** Empty or partially visible query | Report empty/unknown or partial access; use permission details and operator-provided expected telemetry context. Do not label the application healthy or telemetry-free.[^resource-query][^protected] |
| **I** Endpoint/DNS/timeout failure | Network prerequisite blocked for that runtime path; no public-access or DNS relaxation. Authentication and query scope must still be checked after reachability is established.[^private-design] |
| **I** Query syntax/schema/limit failure | Named query-contract failure with nonsecret error details; revise the contract through the operator. Do not swallow errors, broaden table scope, or use unrelated workspace data.[^response][^limits] |

## 9. Unsupported and unresolved cases

These limitations are part of the answer, not reasons to invent a universal role.

| Case | Status and consequence |
| --- | --- |
| No diagnostic setting visible | **F/U:** Cannot conclude no telemetry. Other routes, children, historical records, inaccessible workspaces or delayed ingestion can explain it. Do not enable collection.[^diagnostics][^dcr][^resource-query] |
| Resource logs never collected / only unsupported external sink | **F/I:** No existing Log Analytics evidence path is established. Storage/Event Hubs data access is separate from Monitor reader roles; do not silently add a connector or export/ingestion path.[^monitor-roles][^diagnostics] |
| Missing `_ResourceId`, or an ID belonging to a shared collector | **F/U:** Resource-context cannot be assumed to isolate the selected app. Existing granular conditions may help only with suitable trustworthy fields and permissions; otherwise no demonstrated narrow access path.[^access][^columns][^granular] |
| Non-Azure computers without Arc, Service Fabric, retired classic Application Insights | **F:** Microsoft lists these resource-context limitations; Application Insights resource-context support is for workspace-based resources. No migration/instrumentation during onboarding.[^access][^appinsights] |
| Basic/Auxiliary or long-term-only data | **F/U:** Basic/Auxiliary have no resource-context path. Data outside interactive query retention can require write-requiring search jobs/restoration. Do not assume ordinary `/query` resource access covers either case.[^basic][^access] |
| Azure Monitor workspace / Prometheus rather than Log Analytics | **F/U:** Different metrics store/query model, not a Log Analytics workspace GUID/KQL adapter. Do not infer per-selected-resource Prometheus isolation from the Logs RBAC findings; a separate capability study would be needed.[^monitor-workspace][^private] |
| Existing broad/inherited grants | **F/I:** New narrow assignments cannot reduce them. No proof of isolation until effective access is accounted for; removing unrelated pre-existing grants is not authorized here.[^granular][^scope] |
| Shared component/environment records contain other products' information | **F/U:** Azure resource association does not prove business-content exclusivity. Need provider-specific attribution and content handling, not a group-level Reader grant.[^aca][^apprequests][^columns] |
| Telemetry workspace outside selected subscription/tenant | **F/I:** Azure allows some such routing, but the map excludes it. Record the boundary rather than following references or expanding discovery. Managed identities also do not support direct cross-directory access.[^diagnostics][^mi-faq] |
| Resource-context destination allowlist | **U:** Reviewed resource-query docs describe automatic multi-workspace lookup and permission reporting, not an explicit workspace-exclusion contract. Do not promise that selecting one subscription or passing no additional workspaces pins backend discovery to it.[^resource-query][^request] |
| Protected tables / `DataActionsOnly` detection | **F/U:** June 2026 how-to writes `features.dataAuthorizationMode` as string `DataActionsOnly` using API `2025-02-01`; the `2026-03-01` GET reference describes `dataAuthorizationMode` as **boolean**. This first-party schema discrepancy is unresolved. Treat unknown representations as unknown/unsupported, not false. Protected tables remain documented preview.[^protected][^workspace-new][^access] |
| Granular condition values containing full ARM IDs | **U:** The documented character restriction excludes `/`; a universal `_ResourceId` condition generator is not established. No attempt was made to test service acceptance or rewrite ingestion fields.[^granular] |
| SDK exposes permission diagnostics / Basic query support | **U:** The resource-query REST header is documented; this research did not establish that DSF's installed SDK exposes every permission-detail field or the `/search` surface. The Learn method page also redirects to a preview SDK view. Validate exact dependency capabilities before choosing an adapter; REST support is not proof of current DSF support.[^resource-query][^sdk-resource][^basic][base-query] |

### Decision-ticket inputs and newly sharp questions

Existing tickets already own product/application boundaries (#194), infrastructure
and permissions (#191), evidence readiness (#192), lifecycle (#193), and wizard
behavior (#195). This report does not answer those human decisions.

Potential **new, narrow research questions**, rather than duplicate decision tickets:

1. **Provider attribution compatibility:** For existing Container Apps direct versus
   diagnostic routes and shared Application Insights components, which exact
   table/ID combinations support application-level authorization without changing
   collection? Produce verified positive/negative query fixtures for a later
   explicitly authorized test environment.[^aca][^apprequests][^access]
2. **Granular/preview authorization interoperability:** Which current wire schema and
   API/SDK combinations reliably read `DataActionsOnly`/protected state and enforce
   managed-identity conditions, including slash-containing resource IDs and
   Basic/Auxiliary tables? Resolve the documented mismatches before admitting these
   configurations as supported.[^workspace-new][^protected][^granular][^basic]
3. **Resource-query destination confinement:** Can the direct resource-context API
   enforce an explicit backend-workspace/subscription allowlist, rather than merely
   report selected data sources after lookup? Needed only if compatibility expands
   to resources with routing outside the approved footprint; no scope expansion is
   decided here.[^resource-query]

The central documentary question is answered: discovery and narrowly scoped
observation have documented conditional paths and explicit failure boundaries.
No tenant-specific compatibility or access has been certified. The unresolved
optional/provider-specific combinations above remain unsupported/unknown pending
separate verification; they are not silently assumed to work.

## Sources and verification notes

All external evidence is first-party Microsoft Learn API/CLI/service documentation,
read on 2026-09-14. No repository code/private data was submitted to an external
search service. Important current changes were checked directly: granular RBAC
(published page updated 2025-11-18), protected-table preview/how-to
(2026-06-22), access management (2026-08-25), and workspace API `2026-03-01`
(2026-08-10). The conflicting `dataAuthorizationMode` descriptions are preserved
as uncertainty, not harmonized by guesswork.[^granular][^protected][^access][^workspace-new]

An authenticated public GitHub API request for Azure's REST specs returned an
organization-SAML 403; no credentials or authorization were changed. A guessed raw
spec path returned 404. The Application Insights schema was instead verified in
Microsoft's published ARM reference: Learn's Components GET link for `2020-02-02`
fell back to the older `2015-05-01` view, which lacks the workspace-link field.
That fallback was not used as evidence of absence.[^component-schema][^component-get]

For temporal reproducibility, Learn reported these source revisions for the central
access claims: `manage-access.md` at
`1f73281e8b4b33d9335f6ccadc9ae8de49d5c69c`,
`granular-rbac-log-analytics.md` at
`0d5557bf34f41e1bf87197d76050851c598136e6`,
`protected-tables-configure.md` at
`9d7b314a688847d8f69071c92f4f9a34fed5d6e8`, and the generated workspace GET
reference at `7e07871358020749bc40d4a2ed963ad65c9216a3`.
The public Learn URLs below are the readable sources; their underlying authoring
repositories need not be publicly accessible.

[^account]: [Azure CLI: az account](https://learn.microsoft.com/en-us/cli/azure/account?view=azure-cli-latest#az-account-list), `list`, `--refresh`, `--all`, `show`, and `set`.
[^subscriptions]: [ARM Subscriptions - List, 2022-12-01](https://learn.microsoft.com/en-us/rest/api/resources/subscriptions/list?view=rest-resources-2022-12-01), request and paged result contract.
[^groups]: [ARM Resource Groups - List, 2021-04-01](https://learn.microsoft.com/en-us/rest/api/resources/resource-groups/list?view=rest-resources-2021-04-01), `ResourceGroupListResult.nextLink`.
[^resources]: [ARM Resources - List, 2021-04-01](https://learn.microsoft.com/en-us/rest/api/resources/resources/list?view=rest-resources-2021-04-01), fields, filters and `ResourceListResult.nextLink`.
[^resources-group]: [ARM Resources - List By Resource Group, 2021-04-01](https://learn.microsoft.com/en-us/rest/api/resources/resources/list-by-resource-group?view=rest-resources-2021-04-01).
[^resource-cli]: [Azure CLI: az resource](https://learn.microsoft.com/en-us/cli/azure/resource?view=azure-cli-latest#az-resource-list), `list` and `show` parameter contracts.
[^arg]: [Azure Resource Graph overview](https://learn.microsoft.com/en-us/azure/governance/resource-graph/overview), consistency, provider API versions, permissions, silent partial visibility and throttling.
[^arg-pages]: [Resource Graph: Work with large data sets](https://learn.microsoft.com/en-us/azure/governance/resource-graph/concepts/work-with-data), page size, sorting, truncation and continuation limitations.
[^graph-cli]: [Azure CLI: az graph query](https://learn.microsoft.com/en-us/cli/azure/graph?view=azure-cli-latest#az-graph-query), explicit subscriptions, default scope, extension and paging flags.
[^arg-types]: [Resource Graph supported tables/resource types](https://learn.microsoft.com/en-us/azure/governance/resource-graph/reference/supported-tables-resources), supported-type scope and PII scrubbing.
[^arm]: [What is Azure Resource Manager?](https://learn.microsoft.com/en-us/azure/azure-resource-manager/management/overview), authorization, resource groups, cross-group dependencies and extension resources.
[^scope]: [Understand scope for Azure RBAC](https://learn.microsoft.com/en-us/azure/role-based-access-control/scope-overview), inheritance, scope formats and nested resource example.
[^names]: [Azure resource naming rules](https://learn.microsoft.com/en-us/azure/azure-resource-manager/management/resource-name-rules), case-insensitive names and returned casing caveat.
[^move]: [Move Azure resources: Changed resource ID](https://learn.microsoft.com/en-us/azure/azure-resource-manager/management/move-resource-group-and-subscription#changed-resource-id).
[^providers]: [Azure resource providers and types](https://learn.microsoft.com/en-us/azure/azure-resource-manager/management/resource-providers-and-types), API versions, registration behavior and `/register/action`.
[^slots]: [App Service Web Apps - List Slots, 2024-11-01](https://learn.microsoft.com/en-us/rest/api/appservice/web-apps/list-slots?view=rest-appservice-2024-11-01).
[^management-actions]: [Azure permissions for Management and Governance](https://learn.microsoft.com/en-us/azure/role-based-access-control/permissions/management-and-governance#microsoftresources), resource-group read/write and subscription-resource read actions.
[^monitor-actions]: [Azure permissions for Monitor](https://learn.microsoft.com/en-us/azure/role-based-access-control/permissions/monitor#microsoftinsights), diagnostic, DCR, metric-definition and metric read actions.
[^diagnostics]: [Diagnostic settings in Azure Monitor](https://learn.microsoft.com/en-us/azure/azure-monitor/platform/diagnostic-settings), default collection, destinations, five-setting limit, table creation and resource recreation warning.
[^diagnostic-list]: [Diagnostic Settings - List, 2021-05-01-preview](https://learn.microsoft.com/en-us/rest/api/monitor/diagnostic-settings/list?view=rest-monitor-2021-05-01-preview), existing routing metadata. The API version is explicitly preview.
[^diagnostic-categories]: [Azure CLI: diagnostic-settings categories list](https://learn.microsoft.com/en-us/cli/azure/monitor/diagnostic-settings/categories?view=azure-cli-latest#az-monitor-diagnostic-settings-categories-list).
[^dcra]: [DCR Associations - List By Resource, 2024-03-11](https://learn.microsoft.com/en-us/rest/api/monitor/data-collection-rule-associations/list-by-resource?view=rest-monitor-2024-03-11).
[^dcr]: [Structure of a data collection rule](https://learn.microsoft.com/en-us/azure/azure-monitor/data-collection/data-collection-rule-structure), kinds, input streams, destinations and transformations.
[^workspace-cli]: [Azure CLI: Log Analytics workspace](https://learn.microsoft.com/en-us/cli/azure/monitor/log-analytics/workspace?view=azure-cli-latest), metadata commands.
[^workspace]: [Log Analytics Workspaces - Get, 2025-02-01](https://learn.microsoft.com/en-us/rest/api/loganalytics/workspaces/get?view=rest-loganalytics-2025-02-01), workspace identity, network flags, retention, cap status and Private Link associations.
[^workspace-new]: [Log Analytics Workspaces - Get, 2026-03-01](https://learn.microsoft.com/en-us/rest/api/loganalytics/workspaces/get?view=rest-loganalytics-2026-03-01#workspacefeatures), `WorkspaceFeatures`, including the boolean `dataAuthorizationMode` description.
[^tables]: [Log Analytics Tables - Get, 2026-03-01](https://learn.microsoft.com/en-us/rest/api/loganalytics/tables/get?view=rest-loganalytics-2026-03-01), table schema/configuration and plan.
[^component-schema]: [Microsoft.Insights/components, 2020-02-02 ARM schema](https://learn.microsoft.com/en-us/azure/templates/microsoft.insights/2020-02-02/components#applicationinsightscomponentproperties), `WorkspaceResourceId` and network properties.
[^component-get]: [Application Insights Components - Get, 2015-05-01](https://learn.microsoft.com/en-us/rest/api/application-insights/components/get?view=rest-application-insights-2015-05-01), older GET reference reached by version fallback.
[^appinsights]: [Create and configure Application Insights resources](https://learn.microsoft.com/en-us/azure/azure-monitor/app/create-workspace-resource), workspace integration, retired classic resources and compatible query experience.
[^apprequests]: [AppRequests table reference](https://learn.microsoft.com/en-us/azure/azure-monitor/reference/tables/apprequests), component resource type, role names, resource association, sampling and application-defined fields.
[^aca]: [Container Apps log monitoring](https://learn.microsoft.com/en-us/azure/container-apps/log-monitoring), environment-wide collection, console/system tables, HTTP-log scope and sensitive fields.
[^access]: [Manage access to Log Analytics workspaces](https://learn.microsoft.com/en-us/azure/azure-monitor/logs/manage-access), access/control modes, resource and workspace permissions, protected tables preview and silent empty results.
[^granular]: [Granular RBAC in Log Analytics](https://learn.microsoft.com/en-us/azure/azure-monitor/logs/granular-rbac-log-analytics), minimum Actions/DataActions, additive permissions, conditions/operators, prerequisites, limitations and resource-context FAQ.
[^protected]: [Configure protected tables](https://learn.microsoft.com/en-us/azure/azure-monitor/logs/protected-tables-configure), protection conditions, resource-centric DataAction, DataActionsOnly examples and successful empty queries.
[^columns]: [Standard columns in Azure Monitor logs](https://learn.microsoft.com/en-us/azure/azure-monitor/logs/log-standard-columns), `TenantId`, `_ItemId`, `_ResourceId` and `_SubscriptionId`.
[^query-scope]: [Log query scope](https://learn.microsoft.com/en-us/azure/azure-monitor/logs/scope), resource/workspace differences, resource-scope expression restrictions, regional fan-out and table-plan limits.
[^resource-query]: [Querying logs for Azure resources](https://learn.microsoft.com/en-us/azure/azure-monitor/logs/api/azure-resource-queries), direct resource URL, partial/absent access behavior and `Prefer: include-permissions=true`.
[^basic]: [Query Basic and Auxiliary tables](https://learn.microsoft.com/en-us/azure/azure-monitor/logs/basic-logs-query), workspace-only scope, `/search`, language/time restrictions and query charges.
[^request]: [Logs query API request format](https://learn.microsoft.com/en-us/azure/azure-monitor/logs/api/request-format), workspace GUID, time span and additional-workspace parameters.
[^response]: [Logs query API response format](https://learn.microsoft.com/en-us/azure/azure-monitor/logs/api/response-format), tables and HTTP-200 `PartialError` contract.
[^limits]: [Azure Monitor service limits](https://learn.microsoft.com/en-us/azure/azure-monitor/fundamentals/service-limits#query-api), query output/runtime limits and user throttling.
[^latency]: [Log data ingestion time](https://learn.microsoft.com/en-us/azure/azure-monitor/logs/data-ingestion-time), collector/network/service latency.
[^sdk-resource]: [LogsQueryClient.QueryResourceAsync](https://learn.microsoft.com/en-us/dotnet/api/azure.monitor.query.logsqueryclient.queryresourceasync?view=azure-dotnet-preview), resource-ID query method; documentation currently exposes a preview package view.
[^roles]: [Azure built-in privileged roles](https://learn.microsoft.com/en-us/azure/role-based-access-control/built-in-roles/privileged), Contributor, Owner, RBAC Administrator and User Access Administrator definitions.
[^assign]: [Assign Azure roles using CLI](https://learn.microsoft.com/en-us/azure/role-based-access-control/role-assignments-cli), exact scope, principal object ID, role-definition IDs and new-principal handling.
[^rbac-errors]: [Troubleshoot Azure RBAC](https://learn.microsoft.com/en-us/azure/role-based-access-control/troubleshooting), constrained delegation, directory lookup, recreated identities, propagation and App Service read-only limitations.
[^deny]: [Azure deny assignments](https://learn.microsoft.com/en-us/azure/role-based-access-control/deny-assignments), deny precedence despite an allow role.
[^monitor-roles]: [Roles, permissions and security in Monitor](https://learn.microsoft.com/en-us/azure/azure-monitor/fundamentals/roles-permissions-security), monitoring reader/contributor differences and separate sink-data access.
[^what-if]: [Bicep what-if](https://learn.microsoft.com/en-us/azure/azure-resource-manager/bicep/deploy-what-if), deployment permissions, validation levels and incomplete analysis.
[^mi]: [Managed identities overview](https://learn.microsoft.com/en-us/entra/identity/managed-identities-azure-resources/overview), identity creation/attachment/authorization and lifecycle distinctions.
[^mi-faq]: [Managed identities FAQ](https://learn.microsoft.com/en-us/entra/identity/managed-identities-azure-resources/managed-identities-faq), required roles, identity boundary, explicit selection, caching and cross-directory limitations.
[^identity-practice]: [Azure Identity .NET authentication best practices](https://learn.microsoft.com/en-us/dotnet/azure/sdk/authentication/best-practices#use-deterministic-credentials-in-production-environments), deterministic production credentials.
[^api-auth]: [Logs API access and authentication](https://learn.microsoft.com/en-us/azure/azure-monitor/logs/api/access-api), Entra authentication, hostname migration and token resource.
[^private]: [Azure Monitor Private Link](https://learn.microsoft.com/en-us/azure/azure-monitor/fundamentals/private-link-security), AMPLS, shared endpoints and separation from authorization.
[^private-design]: [Design Azure Monitor Private Link](https://learn.microsoft.com/en-us/azure/azure-monitor/fundamentals/private-link-design), DNS impact, separate ingestion/query access, network topology and ARM query limitation.
[^metrics]: [Azure Monitor REST API walkthrough](https://learn.microsoft.com/en-us/azure/azure-monitor/platform/rest-api-walkthrough), metric APIs and ARM authentication audience.
[^activity]: [Activity Log in Azure Monitor](https://learn.microsoft.com/en-us/azure/azure-monitor/platform/activity-log), default collection, control-plane meaning, retention and resource filters.
[^monitor-workspace]: [Azure Monitor workspace overview](https://learn.microsoft.com/en-us/azure/azure-monitor/metrics/azure-monitor-workspace-overview), Prometheus metrics store versus Log Analytics.

[base-definition]: https://github.com/JoranBergfeld/dark-software-factory/blob/b427b762e7f5a9982b64059c995802d8b8d5920c/dotnet/src/Dsf.Cli/PlannedInstanceDefinition.cs#L72-L91
[base-plan]: https://github.com/JoranBergfeld/dark-software-factory/blob/b427b762e7f5a9982b64059c995802d8b8d5920c/dotnet/src/Dsf.Cli/AzureProvisioningPlan.cs#L78-L93
[base-query]: https://github.com/JoranBergfeld/dark-software-factory/blob/b427b762e7f5a9982b64059c995802d8b8d5920c/dotnet/src/Dsf.Runtime/AzureMonitorIntegration.cs#L17-L65
[base-evidence]: https://github.com/JoranBergfeld/dark-software-factory/blob/b427b762e7f5a9982b64059c995802d8b8d5920c/dotnet/src/Dsf.Runtime/AzureMonitorIntegration.cs#L98-L133
[base-operator-start]: https://github.com/JoranBergfeld/dark-software-factory/blob/b427b762e7f5a9982b64059c995802d8b8d5920c/docs/site/get-started/provision-a-factory.md#L1-L20
[base-operator]: https://github.com/JoranBergfeld/dark-software-factory/blob/b427b762e7f5a9982b64059c995802d8b8d5920c/docs/site/get-started/provision-a-factory.md#L112-L177
[base-registry]: https://github.com/JoranBergfeld/dark-software-factory/blob/b427b762e7f5a9982b64059c995802d8b8d5920c/docs/adr/0021-product-registry-appconfig.md#L27-L50
[base-real]: https://github.com/JoranBergfeld/dark-software-factory/blob/b427b762e7f5a9982b64059c995802d8b8d5920c/docs/adr/0014-real-only-src-no-offline.md#L25-L39
