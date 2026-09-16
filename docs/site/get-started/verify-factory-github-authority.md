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
No publication caller is eligible while inactive; no issue-write token is issued.
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
the signing key nor standing content/PR-write authority. Known inherited
non-authentication workload grants block before copying. Preexisting/shared
workload grants are held for administrator reconciliation, never relabelled as
newly owned or overwritten.

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
effective token repositories, resolves the durable repository, reads the actual
default-branch ref, and verifies the repository's App installation membership.
Renames/default-branch changes update observed proof, not repository settings.
Changed account/installation selection or permissions require review.

Native Entra ingress requires HTTPS and the approved tenant/audiences/object IDs;
its token store is disabled. The application checks the injected principal,
tenant and application-client claims before `POST /tokens/contents`. Only the
approved code-intent workload receives an ephemeral, `Cache-Control: no-store`
read token; it must never persist or log that response. The operator receives
only nonsecret `POST /verify` proof. `POST /tokens/issues` always returns `403`.
Existing A2A `/gather` traffic does not authorize token issuance.

`dsf serve-github-auth` forwards to the bundled runtime. The dedicated host
requires `DSF_PRODUCT`, `AZURE_APPCONFIG_ENDPOINT`, `DSF_FACTORY_PROFILE_PIN`,
and `AZURE_CLIENT_ID`; it is deployed only behind the pinned native authentication
ingress, not as a standalone unauthenticated token service. It does not use
legacy/PAT/operator-token fallback or weaken ordinary inactive runtime admission.

## Durable effects and recovery

The product manifest points to `gitHubAuthorityOperationId`. Unlabelled product
records contain the nonsecret authority and effect receipts:

| Record | Meaning |
| --- | --- |
| `dsf/factory/github-authority` | Exact approved source/destination/version, identity bindings, risk acceptance, capability state and observed proof |
| `dsf/factory/github-operations/<operation-guid>/<0000-index>` | Prior intent, API/input hash, client/provider correlation, exact target and creation evidence |

Five effects are separately recorded: secure-reference key deployment, exact
secret-reader role, product-configuration reader role, authentication host and
native authentication policy. ARM receives a secure parameter reference with
the full source vault resource ID and `secretVersion`; no PEM passes through
operator arguments, temporary files, settings, outputs or receipt hashes.
Destination version and native creation metadata bind the copy receipt.
Secrets/tokens never enter App Configuration, previews, errors or logs.

The owner registry and archived admission plan remain unchanged. There is no
cross-store/ARM transaction or universal create-only ARM fencing. Names and
compatible resources are not creation evidence. Missing/malformed provider
incarnation metadata or incomplete operation inventory remain held.

`status`, `resume`, and repeated `github` without `--verify-only` inspect stored
progress and **historical** proof. They never reissue key copies, grants or
deployments, even after cancellation, lost responses or lease age. Known
in-flight deployments are polled during the original invocation without another
PUT. Uncertain creation effects cannot be forced through or given a new identity.

After all five effects are proven, refresh access evidence explicitly, including
after an administrator resolves existing consent, propagation or network access:

```bash
dsf onboard decide github --verify-only \
  --owner-appconfig-endpoint https://owner.azconfig.io --product widget \
  --tenant 11111111-1111-1111-1111-111111111111 \
  --subscription 22222222-2222-2222-2222-222222222222 --json
```

This rechecks the same bindings, native policy, host incarnation and current
automation, then updates proof conditionally. It issues no infrastructure writes
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
