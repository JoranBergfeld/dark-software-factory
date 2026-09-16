# Reserve durable factory identity

`dsf onboard decide reserve` reserves an immutable factory ID and owner-scoped
observation claims for a **current, complete, explicitly reviewed plan**. It ends
at **Reserved / not provisioned**. It never creates a factory resource, grant,
secret copy, ARM tag, repository, label, issue or PR. Approval is not App consent,
workload access, council readiness or activation. `dsf new` remains separate.

Use the installed tool and the already [prepared owner](bootstrap.md), with
effective `gh` and `az` authentication. No checkout, provisioning asset, runtime
host invocation or local factory-state file is needed. The operator needs owner
App Configuration data-plane read/write access; DSF never grants it.

## Explicit approval, including redirected terminals

First complete [boundary planning](preview-application-boundary.md). Keep its final
normalized `selection` in `selection.json`. Review the categorized plan and copy
its **full** `fingerprint`, not `selectionFingerprint`:

```bash
dsf onboard decide reserve --input selection.json \
  --reviewed-fingerprint <full-current-plan-fingerprint> --json
```

Both options are mandatory even in an interactive terminal. There is no generic
`--yes`, automatic reservation after planning, environment-derived selection or
greenfield fallback. Without `--json`, the same command prints an explanatory
line followed by the typed JSON result; it does not add a consent prompt.

Reservation rereads repository, Azure and owner admission facts through the
existing planning orchestration. Changed declarations, identity, authority,
resource facts, owner observations or plan revisions block earlier approval.
Fingerprints are stale-input checks, not credentials or ownership proofs.

The response includes factory/operation IDs, owner/product and durable repository
identity, actor IDs, admission time, revision and confirmed/pending progress.
`hasRegistryEffects` reports dispatched or potentially applied registry writes
in **this invocation**, not provisioning. An initial failed/uncertain admission
may expose candidate IDs; only a verified remote anchor confirms admission.

## Inspect or resume from another machine

Supply the owner endpoint, product key and tenant/subscription from the approved
selection explicitly:

```bash
dsf onboard decide status \
  --owner-appconfig-endpoint https://owner.azconfig.io --product widget \
  --tenant 11111111-1111-1111-1111-111111111111 \
  --subscription 22222222-2222-2222-2222-222222222222 --json

dsf onboard decide resume \
  --owner-appconfig-endpoint https://owner.azconfig.io --product widget \
  --tenant 11111111-1111-1111-1111-111111111111 \
  --subscription 22222222-2222-2222-2222-222222222222 --json
```

`status` is read-only. It authenticates current owner-store reads and verifies
remote plan chunks/claims, not just a cached count or local report. Its `plan` is
the **admitted snapshot**, not a fresh boundary review. `Reserved` never promises
current workload readiness.

`resume` loads that same immutable remote operation, verifies every present
record, and revalidates the exact plan against current operator authority and
decision inputs before dispatch. Same-operation records are excluded from fresh
admission inspection only after matching verified remote values **and ETags**.
There is no user-controlled ignore-conflicts flag. Changed operator identities
or decision facts block the old review rather than silently amending it.
Already complete, current reservations are not rewritten.

A crash during plan upload can leave some approved bytes missing. Status exposes
partial/unknown progress; resume without those bytes is blocked. If the original
selection can be supplied, add `--input selection.json` to **resume**. It is only
repair data: fresh review, the stored fingerprints, the immutable plan byte
digest, and every surviving chunk must all agree before missing chunks can be
conditionally created. A stale local selection cannot overwrite a revision or
prove ownership. If the original inputs are lost or current facts changed, the
operation stays held and blocked; DSF does not invent the missing plan.

If the first admission has no observable receipt, status/resume remain unknown
until trusted owner state can be established. They cannot create a replacement
identity or assert that an earlier durable write existed. Do not run a fresh
reservation to escape uncertainty, delete records, or manually free claims.

## Durable protocol and bounds

All records use schema version 1, the unlabelled key (`null` in service responses,
`ProductConfigurationKeys.NoLabel` in plan claims), and content type
`application/vnd.dsf.reservation+json;version=1`.

| Kind | Key | Role |
| --- | --- | --- |
| `decide-reservation` | `dsf/onboarding/products/<product>` | Product claim and first durable admission/recovery anchor |
| `decide-plan-chunk` | `dsf/onboarding/plans/<factory-guid>/<0000-index>` | Immutable referenced reviewed-plan bytes |
| `decide-observation-claim` | Plan-proposed repository/resource key | Immutable operation-owned repository/evidence/application claim |

The anchor binds generated UUIDs, owner/product, durable repository ID,
environment, tenant/subscription, selection/full fingerprints, actor IDs and UTC
admission time. Its bounded plan digest/length/chunk count references the full
normalized plan, including exact application/backend/model IDs, tables,
declarations, observed App/installation IDs, resources, grants and review facts.
Plan bytes use the existing canonical ordering and the recorded admission time,
so equivalent provider ordering does not prevent exact-byte upload repair.
Only reviewed projections are persisted: no source files, tokens or private keys.

The product anchor is an atomic **conditional create**, not a fictional prior
write-ahead entry. Once confirmed, every non-anchor write has a durable anchor
intent (stable step, exact key/label and value digest) **before dispatch**.
Steps upload plan chunks, claim the durable repository, then claim canonical
application/evidence resource IDs. Non-product claim steps use ordinal key/label
order both before initial dispatch and after remote reconstruction, independent
of resource-ID or serialized-array ordering. A mismatching pending intent remains
blocked; it is never retargeted to another claim. Each confirmation advances the
anchor through ETag comparison. The final conditional anchor update records `Reserved`.

The Azure SDK uses `AddConfigurationSettingAsync` (`If-None-Match: *`) and
`SetConfigurationSettingAsync(..., onlyIfUnchanged: true)` (opaque `If-Match`
ETag). Automatic retries are disabled. Registry credentials come only from
`az account get-access-token --subscription ... --resource https://azconfig.io --output json`,
with tenant/subscription/object-ID checks; writes bind the reviewed Azure actor.
Explicit JSON output works regardless of the operator's Azure CLI table/YAML
default, without changing that setting. There is no default-credential chain.

This is **per-key+label conditional admission, not a cross-key transaction**.
Partial product/claim records stay held by their recorded operation. Unacquired
claims are not reported as confirmed; competing clients cannot both complete
overlapping reservations. There are no deletes, automatic rollback, leases,
expiry-based release, global fencing, ARM-tag CAS, or tag repairs.
Owner-scoped claims are not cross-owner exclusivity proof.

Each value is budgeted in UTF-8 together with key/label/content-type and a
conservative 1 KiB metadata allowance against App Configuration's **10 KiB**
record limit. Plan chunks contain at most 4,000 original bytes encoded as base64.
An operation is limited to a 1 MiB serialized reviewed plan and 512 total claims;
oversized admission fails before the anchor, without truncation. There is no
single growing owner registry value.

Reservation records never publish `GITHUB_REPOSITORY` /
`AZURE_APPCONFIG_ENDPOINT` runtime index entries. `dsf list` and runtime forwarding
cannot treat them as provisioned products. Unknown/legacy/conflicting onboarding
schemas remain blocked or unknown rather than being adopted as empty state.

## Outcomes and cancellation

| Exit | State / meaning |
| --- | --- |
| `0` | `Reserved`, verified at the indicated revision; not provisioned or active |
| `1` | `Partial`, `Blocked`, `Failed`, or `Unknown`; inspect detail and pending intent |
| `2` | Invalid command grammar, including unsupported force/approval shortcuts |
| `130` | `Canceled`; no further effect dispatch, but prior/in-flight writes may remain |

A definite rejection differs from a lost or malformed response. Invalid receipt
dates or JSON field types remain redacted `Unknown`, even after HTTP success:
the conditional write may already have committed. DSF retains the same operation
and any pending intent rather than marking it failed or replacing its identity.
Malformed reads cannot establish absence or authorize another write. Valid
subsequent reads reconcile trusted state; provider bodies and parse errors are
not printed.

Known step failure is recorded conditionally where possible; inability to confirm
that outcome update is itself unknown. Status reconciles exact immutable records
without rewriting them.
Cancellation does not roll back or release admission. A pending intent may still
have applied, and elapsed time alone never proves that a writer stopped.

The owner store currently owns **pre-provisioning admission/recovery**, not an
editable active-product manifest or publication journal. Subsequent foundation
work must consume this same identity and approved plan, recheck current authority,
and preserve the owner admission pointer when product manifest authority moves.
