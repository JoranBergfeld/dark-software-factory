# Verify factory GitHub authority

`dsf onboard decide github` prepares the approved **direct GitHub App** topology
after a [proven inactive foundation](provision-inactive-foundation.md). It copies
one existing owner signing-key version, grants only the authentication module
access, deploys that module, and asks the deployed host to verify actual GitHub
authority. A successful operator `gh` request is not this proof.

**The factory remains inactive.** Verification creates no issue, PR, label,
commit, settings change, installation expansion or permission upgrade. It does
not acquire or analyze source, enable publication, or activate the council.

## Prerequisites and signing risk

The owner vault administrator supplies the full source vault ARM ID, including
its subscription, and the existing `github-app-private-key` version. The source
must match the reviewed owner vault, tenant and ARM template-retrieval setting.
The deploying operator needs the existing source
`Microsoft.KeyVault/vaults/deploy/action` permission, factory deployment/secret
creation and exact role-assignment rights, and product configuration data access.
The owner key and installation are not rotated, replaced or modified.

For subsequent proposal publication, the existing App **and installation must
already have** `metadata:read`, `contents:read`, `actions:read`,
`repository_hooks:read` and `issues:write` **before this initial authority
review/approval**. Ask the GitHub App/installation administrator to establish
these prerequisites outside DSF before onboarding. The exact original
dictionaries are pinned; adding permissions afterward is a detected authority
change, not an upgrade DSF can perform or silently approve.
An existing narrower pinned factory needs separate reviewed reconfiguration.

Supply an accessible, digest-pinned image built from the DSF runtime containing
`serve-github-auth`. The existing runtime Dockerfile and image workflow include
the implementation; an arbitrary placeholder image is not verification.

Caller authentication uses Container Apps native Entra authentication and an
**existing single-tenant API application** (`--auth-audience`). Its resource
audience is `api://<application-client-id>`. The identity administrator must
already permit the approved operator's Azure CLI to acquire an API bearer and
the approved code managed identity to acquire its application bearer, including
any required existing consent/assignment policy. DSF creates no directory
registration, consent, application role, directory privilege or signing-key
isolation service. Missing prerequisites block; they are not simulated.

The operator needs an approved network/DNS path to the internal Container Apps
endpoint. The auth host needs the foundation's private configuration/vault paths,
managed identity, and outbound GitHub connectivity. DSF does not make the
environment public, repair shared networking or fall back to operator GitHub
credentials when the deployed path is unavailable.

**Accept the real risk:** the key holder can mint broader App-authorized tokens,
including access beyond the normally requested repository and permissions.
Downscoping is normal discipline, **not signing-key containment**. GitHub's
issue-write permission is coarser than DSF's allowed publication behavior.
This original auth host never issues an issue-write token. A later,
separately reviewed [publication capability](prepare-council-preview.md#prepare-the-isolated-native-publication-capability)
can mint a token inside another isolated AUTH host to verify provider readiness,
without authorizing an issue create or changing CODE's token.
This slice does not introduce an owner broker or stronger-isolation project.

## Preview and approve

Redirected terminals and `--json` never prompt:

```bash
dsf onboard decide github \
  --owner-appconfig-endpoint https://owner.azconfig.io --product widget \
  --tenant 11111111-1111-1111-1111-111111111111 \
  --subscription 22222222-2222-2222-2222-222222222222 \
  --source-vault-id /subscriptions/33333333-3333-3333-3333-333333333333/resourceGroups/owner/providers/Microsoft.KeyVault/vaults/owner \
  --source-key-version 0123456789abcdef0123456789abcdef \
  --runtime-image 'ghcr.io/your-owner/dsf-runtime@sha256:<actual-image-digest>' \
  --auth-audience 44444444-4444-4444-4444-444444444444 --json
```

Replace the example identifiers and image digest with reviewed nonsecret values.
The preview exits `1` with `Partial`, without writes. It binds the exact source
version, proven destination and identity incarnations, runtime image, existing
API audience and packaged template hashes. `acceptedSigningRisk` is false.
Repeat the same command with `--approve <gitHubAuthority.approvalFingerprint>`
to authorize those exact effects and accept the disclosed risk.

In an interactive terminal, omit missing values to be prompted. The CLI displays
the complete nonsecret preview before asking for its fingerprint and prints the
equivalent explicit command. Blank approval leaves a read-only preview. There
is no `--yes`, force, local-manifest authority or name-based adoption.

## Deployed checks and workload isolation

Only `factory:github-auth` is mounted in the new container. Its managed identity
gets Key Vault Secrets User at the exact factory secret scope and App
Configuration Data Reader at the product store. Other workloads receive neither
the signing key nor standing content/PR-write authority. Before copying and
during re-verification, [ARM effective-assignment](https://learn.microsoft.com/en-us/azure/role-based-access-control/role-assignments-list-rest)
`atScope()+and+assignedTo('<service-principal-id>')`
queries check each unrelated workload's assignments at the secret and ancestor
scopes, including transitive/nested security-group membership. Azure supports
this lookup for service principals and managed identities; no user-only Graph
lookup or broader directory consent is requested.

Key-reading or access-changing roles block; metadata-only roles do not. Role
definitions, exclusions and applicable scopes are inspected, not just direct
principal IDs. Unreadable/incomplete membership, roles, scopes or unsupported
conditions remain `Unknown`. Unrelated operator/group authority is not attributed
to a workload or adopted. Preexisting authentication grants remain held for
administrator reconciliation, never relabelled as newly owned or overwritten.

The runtime reads the current product manifest, authority and pinned inactive
profile through its selected managed identity. It reads the **pinned factory key
version**, authenticates `GET /app`, checks the intended installation/account,
suspension, selected/all selection and exact granted/requested permissions,
then requests an installation token with:

```json
{"repository_ids":[913],"permissions":{"metadata":"read","contents":"read"}}
```

The repository ID is the approved durable ID, not a name lookup or a discovery
token. GitHub must return selected single-repository reach, exactly those
permissions and an explicit short-lived expiry. The host independently lists
effective token repositories, resolves `GET /repositories/<durable-id>` (the
[official Octokit numeric-ID lookup](https://github.com/octokit/octokit.net/blob/main/Octokit/Clients/RepositoriesClient.cs)), reads
the actual default-branch ref, and verifies the repository's App installation
membership. A second durable-ID read detects metadata changes during observation.
The CLI then reviews current automation and labels against that same durable ID
and compares its metadata with the deployed proof. The historical name is not
an identity lookup: renaming the approved repository and reusing its old name
does not retarget or block an otherwise unchanged installation.
Renames/default-branch changes update observed proof, not repository settings
or the immutable admission plan. Metadata races remain `Unknown`, requiring a
new read-only observation rather than guessed name ownership.
Changed account/installation selection or permissions require review.

Native Entra ingress requires HTTPS and the approved tenant/audiences/object IDs;
its token store is disabled. The application checks the injected principal,
tenant and application-client claims before `POST /tokens/contents`. Only the
approved code-intent workload receives an ephemeral, `Cache-Control: no-store`
read token; it must never persist or log that response. The operator receives
only nonsecret `POST /verify` proof; that endpoint independently requires the
approved operator's native principal and tenant. Code callers cannot invoke it.
`POST /tokens/issues` always returns `403`.
Existing A2A `/gather` traffic does not authorize token issuance.

`dsf serve-github-auth` forwards to the bundled runtime. The dedicated host
requires `DSF_PRODUCT`, `AZURE_APPCONFIG_ENDPOINT`, `DSF_FACTORY_PROFILE_PIN`,
and `AZURE_CLIENT_ID`; it is deployed only behind the pinned native authentication
ingress, not as a standalone unauthenticated token service. It does not use
legacy/PAT/operator-token fallback or weaken ordinary inactive runtime admission.

## Durable effects and recovery

The product manifest pins `gitHubAuthorityOperationId` and
`gitHubAuthorityApprovalFingerprint`. Unlabelled product records contain the
nonsecret authority, original foundation bindings and effect receipts:

| Record | Meaning |
| --- | --- |
| `dsf/factory/github-authority` | Exact approved source/destination/version, identity bindings, risk acceptance, capability state and observed proof |
| `dsf/factory/github-bindings/<operation-guid>/<auth,code,council,source,vault,hosting>` | Original completed/proven foundation receipts, hash-bound to approval; original creation ownership is preserved |
| `dsf/factory/github-operations/<operation-guid>/<0000-index>` | Prior intent, API/input hash, client/provider correlation, exact target and creation evidence |

Five effects are separately recorded: secure-reference key deployment, exact
secret-reader role, product-configuration reader role, authentication host and
native authentication policy. ARM receives a secure parameter reference with
the full source vault resource ID and `secretVersion`; no PEM passes through
operator arguments, temporary files, settings, outputs or receipt hashes.
Destination version and native creation metadata bind the copy receipt.
Secrets/tokens never enter App Configuration, previews, errors or logs.

CLI inspection and deployed token authorization share the same preparation,
binding and completed-effect validation. Token requests require the manifest's
exact approval, six original bindings, all five hash-bound creation receipts and
verified capability proof; missing, corrupt, foreign or stale references deny
issuance. Stage/risk labels alone are insufficient. Operator `/verify` can
observe access without complete effect receipts, but returns no token and writes
no readiness state. The CLI re-reads and validates complete provenance before
publishing `Verified`; incomplete recovery observation is not token readiness.
Older incomplete records stay held, without automatic backfill or replacement.
The runtime repeats current governance and complete-provenance validation after
GitHub access checks, immediately before releasing a read token. A persisted hold
or changed/missing receipt during those checks prevents token release, even when
GitHub access itself still succeeds. Already-issued tokens retain their GitHub
expiry; this is not a distributed lock or token-revocation transaction.

The owner registry and archived admission plan remain unchanged. There is no
cross-store/ARM transaction or universal create-only ARM fencing. Names and
compatible resources are not creation evidence. Missing/malformed provider
incarnation metadata or incomplete operation inventory remain held.

`status`, `resume`, and repeated `github` without `--verify-only` inspect stored
progress and **historical** proof. They never reissue key copies, grants or
deployments, even after cancellation, lost responses or lease age. Known
in-flight deployments are polled during the original invocation without another
PUT. Uncertain creation effects cannot be forced through or given a new identity.

The code identity consumes this authority through
[resumable source inventory](acquire-source-inventory.md), not an operator checkout
or the legacy broad GitHub credential provider.

After all five effects are proven, refresh access evidence explicitly, including
after an administrator resolves existing consent, propagation or network access:

```bash
dsf onboard decide github --verify-only \
  --owner-appconfig-endpoint https://owner.azconfig.io --product widget \
  --tenant 11111111-1111-1111-1111-111111111111 \
  --subscription 22222222-2222-2222-2222-222222222222 --json
```

Before external verification, the CLI conditionally writes a non-authorizing
`Partial` / `host-verification` state while retaining the previous proof as
history. Only confirmation of that exact pending write permits checks to start.
Cancellation or a rejected final-outcome write therefore leaves the confirmed
hold in place, not the old `Verified` authorization. If the initial hold cannot
be confirmed, verification does not start and no revocation is asserted:
the factory App Configuration administrator must reconcile access/current state.

Both original role-assignment IDs are read directly using
[ARM Get By ID, API 2022-04-01](https://learn.microsoft.com/en-us/rest/api/authorization/role-assignments/get-by-id?view=rest-authorization-2022-04-01).
Current principal/type, role definition, scope, condition, condition version,
creator and creation incarnation must match the recorded bindings. Nullable
`conditionVersion` is captured in grant receipts; omitted null values preserve
existing receipt hashes. Missing or changed/recreated grants block; denied,
unreadable or malformed evidence remains `Unknown`, naming the exact GET and
factory Azure RBAC grantor. Inherited or replacement grants that still allow
key/configuration access are not substitutes, and no grant is recreated/adopted.

Verification also rechecks the same base bindings, native policy and host incarnation, obtains
deployed durable-ID evidence, reviews current automation/labels, and only then
updates proof conditionally. It issues no infrastructure writes
or repository mutations; it does mint a fresh restricted token. It cannot repair
incomplete creation receipts, replace a key/host, or accept new preparation inputs.
A failed refresh restores the pending GitHub-authentication capability.
Stored `Verified` is a historical observation, not continuing authorization.
Each token issuance revalidates access; future activation/filing must enforce
their own current governance and eligible-publisher checks.

| Exit | Meaning |
| --- | --- |
| `0` | Observed GitHub authority verified; factory still inactive |
| `1` | Preview `Partial`, or explicit `Blocked`, `Failed`, `Unknown`; inspect exact operation and responsible administrator |
| `2` | Invalid command grammar |
| `130` | Canceled; remote effects may continue and remain held |

Only `GitHub authentication` leaves `foundation.pendingStages` on verification.
Other workload grants, telemetry, models, source analysis, charter, readiness and
activation remain pending. Publication remains proposal-only/create-once when
eventually authorized; this capability supplies no activation or filing approval.

Deterministic CLI/host tests use synthetic credentials and provider transports.
They are not live Azure/GitHub deployment evidence.
