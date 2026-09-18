# Retain and export onboarding evidence

`dsf onboard decide audit` inspects retained evidence without rerunning source
analysis or the council. Historical evidence is not current readiness,
activation approval, or permission to delete factory infrastructure.

## Operator prerequisites

Use the original owner App Configuration endpoint, product, tenant, and
subscription. The effective Azure CLI operator must be able to read the owner
registration, product governance, and private source-progress stores.
Factories with retained previews additionally require native Cosmos point-read
access to the product's `council-preview` and `runs` containers. The command does
not grant these permissions or borrow a runtime identity.

Exports currently require a local Linux filesystem, `stat`, and `id`. Create a
private parent directory owned by the current user with mode `0700`. The export
directory must not already exist. URLs, symbolic links, shared files, and
overwriting an earlier export are not supported.

## Inspect historical evidence

```bash
dsf onboard decide audit \
  --owner-appconfig-endpoint https://OWNER.azconfig.io \
  --product PRODUCT --tenant TENANT_GUID --subscription SUBSCRIPTION_GUID \
  --json
```

`Inspected` reports protected, retained, and eligible record counts. Each record
has a store, key, native revision, content hash, and revision-pinned references.
Use `--page N` with the same four locators for the next page indicated by
`nextPage`. Inspection performs no writes or model calls.

The graph includes the reviewed application boundary, source inventory and
exclusions, analysis and its model receipts, charter publication/adoption
history, deliberate stop requests and their reviewed profile/plan references,
and retained native preview/run records. A pause root without its audit copy
is partial; surviving stop history without its coordinator cannot certify
reference closure. Neither case reopens admission. Missing targets, changed
checksums, incomplete acquisition, uncertain expiry, or unreadable native
records prevent a successful complete-graph result. Sensitive retained content
is withheld rather than silently omitted from an otherwise successful export.

Current source access may be revoked while the operator can still inspect
last-known-good retained evidence. That evidence remains historical.

## Review and create a private export

Create the parent directory yourself, then request a read-only export review:

```bash
install -d -m 0700 "$HOME/dsf-audit"

dsf onboard decide audit \
  --owner-appconfig-endpoint https://OWNER.azconfig.io \
  --product PRODUCT --tenant TENANT_GUID --subscription SUBSCRIPTION_GUID \
  --export "$HOME/dsf-audit/REVIEWED_EXPORT_NAME" --json
```

Review the graph revision and destination. Repeat the exact command with
`--approve FINGERPRINT`, using its `approvalFingerprint`. A changed graph or
destination requires another review. In a redirected terminal, no approval is
inferred and no prompt is issued.

The approved operation records intent before creating the artifact. It writes
owner-only `0600` files inside a `0700` directory:

- `manifest.json`: factory, graph revision, retention context, roots, and hashes.
- `records-NNNN.json`: bounded chunks of original records and internal references.
- `document-NNNN.json`: decoded, readable boundary and charter/preparation
  documents linked to their original chunk identities.

The command rereads the files, verifies privacy, exact content and reference
closure, and rechecks native roots before returning a protected export receipt.
It does not export raw repository copies or retrieve secrets. An interrupted
export may leave its intent and partial private artifact; these are not a valid
receipt, and retrying must not overwrite them.

## Reverify an export

```bash
dsf onboard decide audit \
  --owner-appconfig-endpoint https://OWNER.azconfig.io \
  --product PRODUCT --tenant TENANT_GUID --subscription SUBSCRIPTION_GUID \
  --verify-export "$HOME/dsf-audit/REVIEWED_EXPORT_NAME" --json
```

Verification is read-only. It requires the original protected receipt and an
accessible private artifact matching the current graph and retention context.
Missing or corrupt files, changed permissions, added files, changed native
roots, or incomplete references invalidate the result. `Verified` never
authorizes infrastructure deletion; purge requires its own reviewed operation.

## Review unused-analysis retention

The unused-analysis policy is 90 days. Approval and reached eligibility are
different conditions. Review it with `--approve-policy`, then repeat with the
returned `--approve FINGERPRINT`; supply the same four locators.

For an exact historical analysis revision, use
`--expire-analysis ANALYSIS_REVISION_GUID` to review its eligible step records.
Repeat with that review's approval fingerprint to apply conditional deletion.
Only unprotected historical analysis at the reached boundary can expire.
Adopted, published, current, and referenced evidence remains protected; source
inventory, governance, runs, receipts, and infrastructure are not deleted.

Preview policy `supervised-conveyor-v3` retains the exact captured analysis
before persisting a preview's inputs. COUNCIL calls the explicitly mutating
`/retain-council-context` route; only the authenticated, approved COUNCIL caller
can use it. CODE uses its own existing private progress-store identity to make
a create-only retention claim. Existing context, input, and inspection routes
remain read-only. This claim can retain evidence, never approve activation,
policy, or deletion.

Cleanup competes for the same per-analysis native claim before deleting any
step. If preview retention wins, the operator records protection and deletes
nothing. If expiry wins, the delayed preview cannot capture that analysis.
Admission, lost acknowledgments, or elapsed time never permit bypassing this
interlock. Retained preview/preparation records protect their exact references,
not every unrelated historical analysis. Older runtime execution policies do
not establish this contract and cannot pass the current native audit checks.

The operator needs data-plane create/read/delete permission on the original
private analysis store, plus governance write permission for protected policy
and disposition records. No runtime permission is broadened. Each delete retains an
intended/dispatched/confirmed or uncertain effect record. Partial, denied, lost,
or canceled effects retain the original claim and prohibit blind replay.

## Outcome meanings

| State | Meaning |
| --- | --- |
| `Reviewed` | Exact inputs shown; no export or expiry effects dispatched. |
| `Inspected` | Retained graph or original policy inspected; no current authorization. |
| `Exported` / `Verified` | Private readable artifact and exact closure verified; no deletion approval. |
| `Expired` | Approved eligible analysis records conditionally deleted; infrastructure retained. |
| `Partial` | Complete closure or cleanup not established; retained evidence explains the incomplete operation. |
| `Blocked` / `Unknown` / `Canceled` | Prerequisite, authority, or effect unconfirmed; do not reset or force retry. |

Retained App Configuration, Cosmos, hosting, and other factory resources
continue to incur costs. Audit export or analysis expiry does not detach the
council, stop workloads, activate proposals, or purge a factory.
