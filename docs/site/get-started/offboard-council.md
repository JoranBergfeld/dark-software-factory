# Offboard only the council

`dsf onboard decide offboard` detaches one Decide council. It does **not** delete
the application, repository, factory identity, protected audit, recovery records,
or retained factory infrastructure. Retained Azure resources can keep incurring
charges. Purge and identity retirement are separate reviewed operations.

Use the installed CLI with the original trusted owner locator:

```bash
dsf onboard decide offboard \
  --owner-appconfig-endpoint https://owner.azconfig.io \
  --product widget \
  --tenant 11111111-1111-1111-1111-111111111111 \
  --subscription 22222222-2222-2222-2222-222222222222 \
  --operation 33333333-3333-3333-3333-333333333333 \
  --json
```

The first call is read-only and returns `ReviewRequired`. Review:

- preserved application, repository, shared owner, prerequisite, audit, and
  disabled-factory assets;
- exact proposed factory-only removals;
- ownership or uncertainty blockers;
- continuing Azure and recoverable-vault costs;
- inherited, shared, token, installation, and key-copy residual authority.

Approve only the returned current fingerprint:

```bash
dsf onboard decide offboard \
  --owner-appconfig-endpoint https://owner.azconfig.io \
  --product widget \
  --tenant 11111111-1111-1111-1111-111111111111 \
  --subscription 22222222-2222-2222-2222-222222222222 \
  --operation 33333333-3333-3333-3333-333333333333 \
  --approve <detach-plan-fingerprint> \
  --json
```

Redirected and `--json` invocations never prompt. All locators and a nonempty
operation ID are mandatory. There is no `--yes`, force, tag-adoption, resource
group adoption, global credential revocation, infrastructure deletion, or
permissive fallback.

## Stop and cleanup semantics

The first confirmed product offboarding record closes runtime admission across
manual runs, sweeps, HTTP hosts, source agents, loops, resumed work, and outcome
polling. A durable owner pointer keeps the detached factory discoverable.
Repeated calls from another machine use the same operation and plan; a competing
operation cannot replace it. Every cleanup or recovery invocation, including a
resume of an existing partial detach, requires `--approve` with that retained
plan fingerprint. Omitting approval returns `ReviewRequired` and performs no
cleanup.

Pause, reconfiguration, and offboarding admission share one conditional
lifecycle coordinator. Only one can win admission. Any non-completed
reconfiguration coordinator or retained effect is an explicit quiescence
blocker; offboarding remains `Blocked` or `Partial` without claim/configuration
cleanup until that transition is terminal. A completed reconfiguration permits
a newly reviewed detach, while a detached lifecycle never permits later
reconfiguration dispatch.

Completion requires confirmed admission closure, consistent audit readback, and
no unresolved issued publication effects. Lost responses, delayed writes,
changed authority, unknown ownership, denied cleanup, or conflicting records
remain `Partial`, `Blocked`, or `Unknown`. Safe independent progress remains
visible; elapsed time and cancellation never certify quiescence.

Partial onboarding can be detached before product-manifest creation using the
trusted owner reservation and immutable recovery evidence. Local selections,
names, tags, or resource-group matches never become ownership proof.

Only provenance-proven sole-factory grants, destination key copies, and eligible
factory claims may be removed after current identity/incarnation checks. Reused,
shared, inherited, recreated, or human-modified assets stay retained and are
reported as residual authority. DSF never rotates/deletes the shared owner key,
uninstalls the shared GitHub App, changes installation-wide repository access,
or promises global invalidation of other key copies or already-issued tokens.

## Outcomes

| Exit | Meaning |
| --- | --- |
| `0` | `Detached`; runtime closed, retained registration confirmed, no purge |
| `1` | Review required, partial, blocked, failed, or unknown; follow `nextAction` |
| `2` | Invalid command grammar |
| `130` | Canceled; remote effects may still exist |

Keep the JSON result with the operation ID, approved fingerprint, completed and
blocked removals, retained-cost disclosure, and next actor/action. Provider
details, credentials, source contents, and secret values are never output.
