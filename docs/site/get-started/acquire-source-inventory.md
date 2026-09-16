# Acquire resumable source inventory

`dsf onboard decide source` acquires a pinned repository inventory through the
factory's [verified readonly GitHub authority](verify-factory-github-authority.md).
It does **not** analyze intent, call models, generate a charter, publish proposals,
activate the conveyor, or execute repository code. The factory remains inactive,
registry-owned and proposal-only; application resources receive no tags.

## Prepare the isolated workload

Prerequisites: a proven inactive foundation, current verified GitHub authority,
the original code identity, hosting environment, network and private DNS creation
receipts, and an accessible digest-pinned image built from the current DSF runtime
Dockerfile. The image must contain `serve-source`.

An Entra administrator supplies an existing single-tenant API client ID with
operator and code-identity consent. The operator must reach private factory
ingress. The code workload needs private App Configuration access, ARM metadata
access and HTTPS access to the approved authentication host and GitHub API.
DSF does not create registrations/consent or relax public networking.

On an interactive terminal, run `dsf onboard decide source --prepare`. Missing
locator, image and audience inputs are prompted. Review the displayed fingerprint;
blank approval leaves a read-only preview. An equivalent explicit command is
printed. Fully explicit preview:

```bash
dsf onboard decide source --prepare \
  --owner-appconfig-endpoint https://owner.azconfig.io --product widget \
  --tenant 11111111-1111-1111-1111-111111111111 \
  --subscription 22222222-2222-2222-2222-222222222222 \
  --runtime-image 'ghcr.io/your-owner/dsf-runtime@sha256:<actual-digest>' \
  --source-audience eeeeeeee-eeee-eeee-eeee-eeeeeeeeeeee --json
```

Replace illustrative IDs/digest with reviewed native values. Repeat with
`--approve '<source.approvalFingerprint>'`. `--json` and redirected terminals
never prompt; supply every locator and preparation input explicitly.

Approval permits exactly eight create-once effects: a private Standard source
App Configuration store, its private endpoint and DNS group, three scoped grants,
one code-only Container App and its native Entra authentication policy. The code
identity gets factory resource-group metadata Reader, product configuration Data
Reader, and Data Owner **only** on `dsfs-<factory-id-without-hyphens>`.
It receives no governance writer, Cosmos access, signing key, operator token or
GitHub issue-write authority. Additional store, endpoint and host costs apply.

Original foundation receipts are pinned into the source review. Names alone are
not ownership: network/DNS incarnations, exact grants, code identity, host
configuration, governance and GitHub authority are rechecked. Where a foundation
network originally supplied only an ETag, preparation first verifies that receipt,
then pins its native resource GUID when available. Unsupported or changed
incarnation evidence blocks rather than adopting infrastructure.
New network children can use native ETag fingerprints when `systemData` is absent,
only alongside their original affirmative creation/operation receipts. A changed
ETag holds access; it is not permission to adopt a replacement.

Preparation returns **Partial**, exit 1, even when `source.stage` is `prepared`:
deployment is not source acquisition. Missing bundled `onboard-source.json` fails
before provider access. Ambiguous deployment responses/receipts stay held; repeat
preparation never recreates, rolls back or repairs grants.

## Advance, inspect, resume

```bash
dsf onboard decide source \
  --owner-appconfig-endpoint https://owner.azconfig.io --product widget \
  --tenant 11111111-1111-1111-1111-111111111111 \
  --subscription 22222222-2222-2222-2222-222222222222 --json
```

Each invocation advances bounded work. Repeat the same command to resume.
`--status-only --page 0` reads coverage without advancement; follow `nextPage`
using `--status-only --page <number>`. Interactive acquisition prompts for missing
locators, not for permission to analyze or execute code.

The CLI calls authenticated `POST /acquire` or `POST /status` on the deployed
`serve-source` host. The host uses explicit code managed identity, obtains short-lived
repo-restricted contents/metadata-read tokens from `/tokens/contents`, and refreshes
them through that same authority. Tokens are never persisted.
The managed-identity request remains `api://<approved-client-id>/.default`.
Native Entra v1 custom-API tokens use `appid` with that API's URI or GUID audience;
v2 tokens use `azp` with the API's GUID audience. Token version, code client/principal,
tenant, managed-identity resource and lifetime must all match; other audiences and
malformed/version-incompatible claims fail closed. Azure service audiences remain
separately constrained to their requested resource URI. These checks supplement,
not replace, current authority and native Entra authentication.
See the [native access-token claims contract](https://learn.microsoft.com/en-us/entra/identity-platform/access-token-claims-reference).

GitHub durable repository ID resolves the actual current name/default branch.
The snapshot pins account/repository ID, observed default branch, commit and root
tree. Nonrecursive immutable Git tree APIs are walked by path, including dotfiles,
ignored-but-tracked files and repeated tree/blob hashes at different paths. Git
object hashes, modes, strict UTF-8 paths and blob lengths/content are checked.
Neither search, changed-file lists nor embeddings substitute for inventory.
A truncated nonrecursive directory is explicitly incomplete: this API has no
invented pagination or completion fallback.

Acquisition only starts after complete tree enumeration. A changed branch/head
blocks incompatible selection. After reviewing that change, add `--new-snapshot`
to explicitly select it; old bounded metadata remains historical. The same
unchanged identity resumes existing progress instead of silently erasing it.
Status also rejects incompatible current selection. A durable repository rename
does not retarget by old name; a metadata race blocks the response.

## Coverage, exclusions and limits

Output identifies path/blob and byte-range/chunk hashes, acquired/excluded/failed/
pending states, expected chunk counts, tree completeness and necessary gaps.
`analysis` is always **NotYetAnalyzed**; `deployedSourceCorrelation` is independently
**Unknown**, never invented or required for acquisition.

Generated, vendor, binary, sensitive, unavailable and external exclusions are
distinct. Known credential paths are excluded before reading. Unexpected supported
secret patterns suppress the entire permitted blob, including its chunk records.
Sensitive tracked-path/branch metadata is suppressed as an explicit inventory gap,
not persisted to satisfy completeness. UTF-16/32-marked text is an unavailable
source gap, not silently treated as irrelevant binary.
This conservative heuristic is not a guarantee of detecting every possible
secret. Only UTF-8 text is acquired; opaque or unsupported content is not analyzed.
No language-specific source whitelist is assumed.

Symlinks and submodules are inventoried but never followed. LFS pointers are not
their objects. External repositories/hosts/objects, target instructions, public
research and repository tests/builds/installers/generators/macros/agents are never
followed or executed. `AGENTS.md` in the target is inert input, not instructions.

| Bound | Limit |
| --- | --- |
| One host request / worker lease | 45 seconds / 60 seconds |
| CLI response deadline | 75 seconds, including response and final authority checks |
| New nodes / content cursor steps per advance | 32 / 32 |
| Acquired chunks per advance | 4 |
| Blob / byte-range chunk | 256 KiB / 16 KiB |
| Tree HTTP response / tracked path encoding | 4 MiB / 2048 UTF-8 bytes |
| Coverage page | 32 path entries |
| Inventory nodes, including root/directories | 32,768 |
| Durable records / cumulative reserved metadata | 65,536 / 64 MiB |
| Each record, including conservative overhead | 8192 bytes, below App Configuration's 10 KiB limit |
| Source selections per operation | 8 |
| Native progress listing | 1024 pages, 4 MiB per page, within the same 45-second request budget |

Limits, unread necessary source, unavailable objects and missing chunks never
become 100% completion. Generated/vendor/binary exclusions are not read source;
inspect exclusions even when all eligible source is acquired. Unavailable path
results are immutable for that selection, not automatically retried as successes.
Large/unsupported inventories remain visibly partial; this slice does not raise
limits or reset failed selections automatically.

| State | Meaning |
| --- | --- |
| Acquired | Complete pinned tree and eligible content accounted for; exit 0, not analysis/readiness |
| Partial | Pending work, bounded deadline, truncation, capacity or necessary source gaps |
| Blocked | Missing/revoked/currently incompatible authority or source identity |
| Failed | Definite immutable-object identity/content failure |
| Canceled | Caller cancellation; remote effects/hold may still be unconfirmed; exit 130 |
| Unknown | Unconfirmed write/response, corrupt/missing progress, or uncertain final authority |

Other outcomes exit 1. After interruption, inspect trusted status and wait for an
active metadata lease to expire before resuming. Lease expiry permits a new
metadata worker, **not** an ARM retry or assumed rollback/revocation. Conditional
ETag fencing protects checkpoints; lost writes are never blindly retried.

## Durable provenance and temporary bytes

Only bounded metadata lives in the separate remote source store. The root
`source/<operation-id-N>/current` holds snapshot, cursors, counters, capacity
reservations, integrity commitment and lease. A key-only native App Configuration
query must prove `source/*` empty before initialization. A conditional immutable
`source/<operation-id-N>/initialized` record reserves both initial records before
creating `/current`; concurrent or uncertain initialization is never overwritten.
Missing `/current` alongside any namespace progress is **Unknown**, not a pristine
store or permission to reset lifecycle counters. An initialization marker without
a confirmed root remains held for administrator reconciliation.

Immutable `source/<snapshot-id>/nodes|results|history/...`
and `source/<snapshot-id>/chunks/<path-index-D8>/<chunk-index-D4>` records retain
path/range provenance. History keys use the zero-based **selection occurrence**,
not a fixed slot: A-B-A-C preserves both A observations without overwriting either.
History values contain the snapshot and its integrity commitment; revisiting
compatible source retains the original content records and proof. All occurrences,
history/proof records and conservative capacity reservations count against the
same lifecycle budgets. Capacity is reserved before each immutable write.

Every global **Acquired** observation requires a fresh, bounded native paged scan
of the namespace, verification of all current nodes/results/chunks and aggregate
counts, and comparison of their values/ETags against the immutable
`source/<snapshot-id>/integrity/00000000` commitment and authoritative root.
The display page never determines completion. A missing or modified record on
another page invalidates the aggregate. `--status-only` performs these reads
without writes, repairs, new proofs or clearing holds.

The SDK's native `Link` continuation is followed only on the same source endpoint,
with the original namespace/label filters; an empty intermediate page is not
completion. Missing, malformed, duplicate or incomplete listing evidence remains
unconfirmed. Interrupted verification restarts a **fresh** scan on another
invocation; no cross-invocation cached-completion flag substitutes for unread pages.
If the complete scan cannot fit the time/listing/metadata bounds, no global success
is returned. Root ETags fence cooperating workers, not a cross-record transaction
against administrative mutations after an observation.

Current serialized progress requires `integrityProtocol: 1`; earlier unsealed
checkpoints are held **Unknown**, not silently migrated or assigned guessed
accounting/proofs. No automatic deletion, reset or reconstruction is provided.
Workers on other machines deserialize these records; local manifests/caches and
shared object aliases are not authority. There is no full-source archive or
persisted excerpt in this slice. Temporary downloaded byte buffers are cleared.

For a subsequent compatible consumer, Core exposes `SourceSnapshot`,
`SourceProgress`, `SourceChunkReference` and `IFactorySourceMaterializer`.
Authenticated code-only `POST /materialize` accepts
`{ "snapshotId": "...", "pathIndex": 1, "chunkIndex": 0, "sha256": "..." }`.
It reacquires the pinned blob, checks safe content and the exact range hash, then
rechecks the currently selected repository/branch/head identity and current
authority before exposing bytes with `Cache-Control: no-store`. Changed source,
access, authority or cancellation suppresses output and clears temporary bytes.
Operators cannot materialize through this route. In-process consumers dispose
`SourceMaterializedChunk` to clear its buffer. Fixed byte ranges can split UTF-8
code points: use a streaming decoder/reassemble contiguous ranges.
Acquired flags never imply retained bytes, inferred intent or model analysis.
