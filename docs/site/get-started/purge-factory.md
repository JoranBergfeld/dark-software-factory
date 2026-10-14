# Purge a detached factory

`dsf onboard decide purge` is a separately reviewed destructive operation. It
removes only current, provenance-proven factory resources and permanently retires
the immutable factory ID. It never reattaches a factory.

First detach the council and produce a current readable private audit export.
Then request a read-only purge plan:

```bash
dsf onboard decide purge \
  --owner-appconfig-endpoint https://owner.azconfig.io \
  --product widget \
  --tenant 11111111-1111-1111-1111-111111111111 \
  --subscription 22222222-2222-2222-2222-222222222222 \
  --operation 33333333-3333-3333-3333-333333333333 \
  --audit-export /private/dsf/widget-audit \
  --json
```

Review the exact resource IDs, creation operations, incarnations, preserved
assets, audit revision, blockers, residual effects, and fingerprint. Purge uses
its own approval; onboarding, activation, export, pause, or detach approval never
authorizes deletion.

```bash
dsf onboard decide purge \
  --owner-appconfig-endpoint https://owner.azconfig.io \
  --product widget \
  --tenant 11111111-1111-1111-1111-111111111111 \
  --subscription 22222222-2222-2222-2222-222222222222 \
  --operation 33333333-3333-3333-3333-333333333333 \
  --audit-export /private/dsf/widget-audit \
  --approve <purge-plan-fingerprint> \
  --json
```

Redirected and JSON invocations never prompt. Every locator, operation ID, export
path, and approval is explicit.

## Safety contract

- Completed detachment is mandatory.
- Current audit roots must match a readable, owner-only, reference-closed private
  export. A request, public URL, stale receipt, corrupt/incomplete artifact, or
  inaccessible backup is not a disposition.
- An explicitly approved retention policy can authorize disposal only after its
  reached expiry and only when every record is eligible. Ordinary 90-day unused
  analysis expiry never erases protected or referenced evidence.
- Every deletion is rechecked against exact resource ID, creation evidence,
  incarnation, and current modification state.
- Reused, shared, unproven, recreated, human-modified, denied, or unknown assets
  remain preserved.
- Parent/resource-group deletion is never inferred from a name, tag, or group
  membership. Parents with unowned additions remain preserved.
- Application resources, repository content/settings, shared owner
  App Configuration/Key Vault/GitHub App installation, and reused model/network
  prerequisites are outside purge.
- Deletion is conditionally journaled. Missing or uncertain acknowledgements are
  `Partial` or `Unknown`, never success and never an unconditional replay.
- Recovery uses the same operation ID and reviewed fingerprint. Durable owner-side
  request/effect records reconcile confirmed absence, preserve recreated objects,
  and block competing operations; they never repeat an uncertain dispatch.
- Audit-backing App Configuration and Cosmos resources are ordered last. Immediately
  before their first deletion, purge retains the final verified authority, audit-root,
  export/disposition, plan, and lifecycle boundary owner-side. Recovery then checks
  only that exact boundary, effect journal, ownership, and remote incarnation; it does
  not require data-plane reads from stores already deleted by the reviewed operation.
- Provider soft delete, recoverable vault custody, backups, and issued tokens are
  reported honestly; purge does not promise cryptographic invalidation or
  physical erasure.

After confirmed deletion, the owner product record becomes a minimal nonsecret
retirement/audit pointer. It retains factory ID, purge operation, approved plan
fingerprint, audit revision/disposition, actor, and time—never credentials, raw
source, export path, or secret values. The old factory ID cannot resume or be
reattached. A later onboarding must use a new identity and cannot bypass unresolved
old claims or effects.

Fresh onboarding must perform a new complete owner-admission review that observes the
exact retirement pointer. Only after the old plan, request, final boundary, confirmed
effects, and released repository/resource claims are verified is the product key
conditionally assigned a new factory ID. Permanent retirement history remains under
the old factory ID; any race, unreadable record, or unresolved effect blocks replacement.

## Outcomes

| Exit | Meaning |
| --- | --- |
| `0` | `Purged`; eligible deletions confirmed and factory ID retired |
| `1` | Review required, partial, blocked, failed, or unknown |
| `2` | Invalid command grammar |
| `130` | Canceled; remote effects may already exist |

Keep the JSON result. It distinguishes deleted, preserved, blocked, residual, and
retired outcomes and gives the next action without relying on color or prompts.
