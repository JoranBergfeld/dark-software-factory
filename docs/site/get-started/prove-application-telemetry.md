# Prove scoped application telemetry

`dsf onboard decide telemetry` deploys the isolated Azure Monitor source after a
[proven inactive foundation](provision-inactive-foundation.md), then requests
nonsecret proof from its **actual managed-identity/network path**. Infrastructure
creation, operator workspace access and successful role assignments are not proof.
The factory stays **Inactive**; this capability cannot gather through the conveyor,
publish, activate, alter application diagnostics or supply synthetic evidence.

## Supported boundary and prerequisites

The immutable reviewed application boundary must explicitly include workspace-based
Application Insights components. Their native `WorkspaceResourceId` mappings must
point to the separately selected, dedicated Log Analytics backends. Selecting a
web app alone does not authorize guessing its instrumentation settings or adopting
a telemetry component. No credential/settings inspection is used to infer a mapping.

Supported tables are Analytics-mode `AppRequests`, `AppDependencies`,
`AppExceptions`, `AppTraces` and `AppEvents`. Other tables, Basic/Auxiliary plans,
classic Application Insights and resource-permission query modes remain NOT READY.
Workspace metadata must explicitly establish workspace-context access
(`features.enableLogAccessUsingOnlyResourcePermissions: false`). DSF never changes
this mode. The boundary planner's wider table inventory is not readiness support.

The CLI rechecks current resources, environment/dedication conflicts, component
workspace mappings, the complete component inventory and selected table modes.
A workspace backing an unselected component is rejected even if a filter could
hide those records. The accountable owner's dedication/exclusivity review remains
an explicit assumption, not a technical global ownership lock. Native component
attribution does not establish deployment/commit correlation or coverage of all
the other application resources.

| Administrator | Exact prerequisite or handoff |
| --- | --- |
| Application/evidence owner | Already selected dedicated components/workspaces and supported Analytics tables; review genuine telemetry cadence. No repairs, additional scopes, tags, synthetic records or diagnostic changes by DSF |
| Azure RBAC/directory grantor | Inspect current source principal, transitive/inherited assignments and exact role/scope bindings; separately establish `Microsoft.Authorization/roleAssignments/write` at each displayed scope, subject to existing deny/conditional policy |
| Factory administrator | Preserve original foundation source MI and internal hosting incarnations; permit creation of only the displayed source host and native auth policy |
| Image administrator | Supply an accessible digest-pinned image built from the current DSF runtime Dockerfile, containing `serve-telemetry`; a placeholder container is not sufficient |
| Entra API administrator | Supply an existing single-tenant API application client ID and existing operator consent for `api://<client-id>`; DSF creates no registration, directory permission or consent |
| Network administrator | Existing operator connectivity/DNS to the private factory ingress, and source connectivity to product App Configuration and Azure Monitor (including any firewall, AMPLS/private-link restrictions); DSF changes no application/shared network |

Only the proven `factory:source-azuremonitor` MI is attached to the host. It receives
App Configuration Data Reader at the product store and Log Analytics Reader at
each approved workspace. Preview lists exact deterministic assignment IDs,
principal IDs, scopes and role definitions. No vault/signing-key, GitHub token,
Cosmos/state, delegation or unrelated workload access is granted.

Effective assignment inspection includes subscription assignments and inherited
authority at each approved scope using ARM `assignedTo`/`atScope`. Incomplete
inventory, unsupported conditions, inherited/group, unrelated or preexisting
assignments are held for the grantor, not adopted. Successful queries do not
replace missing or recreated exact approved grants. Existing role definitions and
their exclusions are checked, including the Log Analytics Reader shared-key
exclusion. That built-in role also includes backend export-read permission; the
source performs only the fixed query API operations, never export jobs. See
[the current built-in role](https://learn.microsoft.com/en-us/azure/role-based-access-control/built-in-roles/monitor#log-analytics-reader).

## Interactive and explicit operation

On an interactive terminal, run `dsf onboard decide telemetry`. Missing locator,
image and audience inputs are prompted. The operator explicitly reviews the
positive freshness window (blank selects the displayed default **24 hours**),
complete configuration and its fingerprint. Blank approval makes no writes.
The CLI prints the equivalent explicit command.

Redirected terminals and `--json` never prompt. A complete read-only preview:

```bash
dsf onboard decide telemetry \
  --owner-appconfig-endpoint https://owner.azconfig.io --product widget \
  --tenant 11111111-1111-1111-1111-111111111111 \
  --subscription 22222222-2222-2222-2222-222222222222 \
  --runtime-image 'ghcr.io/your-owner/dsf-runtime@sha256:<actual-digest>' \
  --auth-audience 77777777-7777-7777-7777-777777777777 \
  --freshness-hours 24 --json
```

Replace example identifiers with existing reviewed values. Review
`telemetry.configuration` and `telemetry.approvalFingerprint`, then explicitly
approve the same complete inputs:

```bash
dsf onboard decide telemetry \
  --owner-appconfig-endpoint https://owner.azconfig.io --product widget \
  --tenant 11111111-1111-1111-1111-111111111111 \
  --subscription 22222222-2222-2222-2222-222222222222 \
  --runtime-image 'ghcr.io/your-owner/dsf-runtime@sha256:<actual-digest>' \
  --auth-audience 77777777-7777-7777-7777-777777777777 \
  --freshness-hours 24 --approve '<telemetry.approvalFingerprint>' --json
```

The default is not an assumption that an application emits daily. Fractional hours
are supported when they represent positive whole seconds, up to 8760 hours.
For example, `--freshness-hours 0.5` reviews 30 minutes. Nonpositive/nonfinite
windows fail before provider access. Changes require a different reviewed
configuration, never silent reuse of the old approval. This create-once capability
does not implement reconfiguration of an existing preparation.

## What the source proves

The private host exposes only `POST /proof`, behind native Container Apps Entra
authentication. Only the originally approved operator may call it. It shares the
factory's pinned product-governance and native caller checks, not the unauthenticated
A2A path. The selected user-assigned `ManagedIdentityCredential` is used for
configuration and Logs queries; no operator/developer credential fallback exists.
The query token's native principal, tenant, client and `xms_mirid` must match the
proven source identity. The CLI itself never queries application logs.

For every selected backend/table the versioned query is:

```kusto
AppRequests | project TimeGenerated, _ResourceId, _ItemId, TenantId, Type
```

The table varies only over the reviewed allowlist. The SDK sends the exact UTC
start/end range, not a trailing implicit service default. There is no KQL scope
filter, cross-workspace expression, external query or invented attribution column.
Native `TenantId` is the **workspace ID**, not the Entra tenant; `Type` must match
the queried table. `_ResourceId` must identify an approved mapped component.
See [Azure Monitor standard columns](https://learn.microsoft.com/en-us/azure/azure-monitor/logs/log-standard-columns).

The full selected-window response must have the expected native schema and be
complete. Partial errors, extra tables, malformed rows and result-limit responses
are rejected, never silently dropped or treated as a page: Logs has no continuation
token. At least one genuine in-window record is required **per selected backend**;
all selected tables are checked, including empty ones.

For an empty table, the separately displayed/versioned stale query uses
`top 1 by TimeGenerated desc` with an explicit Unix-epoch-to-window-end range.
It retrieves only native reference metadata to distinguish no retained record
from an old record. It never makes an old record count as fresh or diagnoses health.
An inconsistent fresh result appearing only in that probe is incomplete evidence.

`Empty`, `Stale`, `PartialResponse`, `WrongScope`, `Unsupported`,
`AmbiguousAuthority`, `PermissionDenied`, `NetworkUnavailable` and malformed
responses are not successful readiness. One record does **not** mean every
component or code path was observed. Optional sources and factory telemetry cannot
substitute for this mandatory application proof.

## Durable records, current checks and recovery

The authoritative product manifest pins `telemetryOperationId` and
`telemetryApprovalFingerprint`. The original owner admission plan stays unchanged.
All records are unlabelled, conditional and bounded by the 10 KiB service budget;
oversized/incomplete data is rejected, not truncated.

| Record | Meaning |
| --- | --- |
| `dsf/factory/telemetry` | Immutable reviewed source/backend/query/window configuration, foundation bindings, exact grants, image/template revision, current capability state and hash-bound proof references |
| `dsf/factory/telemetry-operations/<operation>/<index>` | Pre-dispatch request/target/API intent, client/provider correlations, exact creation/incarnation receipts; only proven creations are newly owned |
| `dsf/factory/telemetry-proofs/<operation>/<check>` | Immutable observed state/reason, source principal, configuration revision, application/backend/table IDs, exact observation/window/record times, native item references, attribution and coverage limitations |

The CLI projects the last verified proof into `telemetry.proof` for inspection.
It is stored separately, not duplicated into the authority record. Failed
observations have their own `lastCheckReference`; previous success is retained as
history. Expired records produce current `Blocked`/`Stale` status, without rewriting
history or making status mutating. Freshness is also checked before final
promotion: evidence expiring during the check remains historical, not current Ready.
Proof exchange must finish within five minutes of the source's window end
(up to one minute of positive clock skew). This bounds proof exchange, not the
application's reviewed emission cadence.

After an administrator resolves a prerequisite, resume **checking**, not creation:

```bash
dsf onboard decide telemetry --verify-only \
  --owner-appconfig-endpoint https://owner.azconfig.io --product widget \
  --tenant 11111111-1111-1111-1111-111111111111 \
  --subscription 22222222-2222-2222-2222-222222222222 --json
```

No new image/audience/window/approval inputs are accepted with `--verify-only`.
First the CLI conditionally holds readiness as `Partial`/`checking` with a new
check ID. It then inspects current bindings/grants/policy, invokes the real host,
and repeats the current checks before recording proof and conditionally promoting
the result. The host independently re-reads pinned governance and complete effect
receipts after querying. Missing or changed exact grants cannot be excused by
working access. Cancellation or failed final persistence leaves the confirmed
hold, not an old Ready cache. An unconfirmed initial hold permits no check and
claims neither successful revocation nor an atomic global stop.

`status`, `resume` and repeated `telemetry` without preparation inputs only inspect.
No unknown grant/host operation is retried, recreated, adopted or rolled back.
Known in-flight deployment responses are polled during the original invocation.
Unknown effects keep reservations and grants held; the administrator must reconcile
the recorded provider operation. ARM/registry writes are not a global transaction.

Exit `0` means the telemetry check was observed ready, **not council readiness or
activation**. Exit `1` covers read-only preview/`Partial`, `Blocked`, `Failed` and
`Unknown`; `2` is grammar failure; `130` is canceled, with issued effects retained.
Only the evidence-access pending stage is cleared. Models, source analysis,
charter, supervised preview and publication remain separate capabilities.

Installed packages include `onboard-telemetry.json` and the matching runtime.
`dsf serve-telemetry` forwards to that runtime and requires `DSF_PRODUCT`,
`AZURE_APPCONFIG_ENDPOINT`, `DSF_FACTORY_PROFILE_PIN` and `AZURE_CLIENT_ID`.
Do not expose it without the reviewed native ingress policy or deploy its template
outside the receipt protocol. Greenfield Azure Monitor gathering remains unchanged.
Deterministic CLI/host tests are not live Azure deployment evidence.
