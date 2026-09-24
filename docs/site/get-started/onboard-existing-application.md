# Onboard an existing application (Decide preview)

`dsf onboard decide preview` shows what attaching a proposal-only Feature Council to an
**existing** Azure application would do. It is read-only: it creates no reservation, factory
resource, role assignment, secret copy, label, issue, or pull request.

This is not [`dsf new`](provision-a-factory.md). Greenfield provisioning is unchanged; the
onboarding profile never rebuilds, redeploys, retags, or takes over your application.

## Prerequisites

- `az login` completed as the identity that can read the application's subscription. The
  preview uses that identity for discovery only.
- An owner control plane from [`dsf bootstrap`](bootstrap.md). A missing owner App
  Configuration or Key Vault is reported as an administrator handoff; onboarding never
  creates owner-wide infrastructure implicitly.

## Preview an exact selection

```bash
dsf onboard decide preview \
  --product shop \
  --tenant <tenant-id> \
  --subscription <subscription-id> \
  --app-environment production \
  --application-resource /subscriptions/<sub>/resourceGroups/rg-shop-api/providers/Microsoft.Web/sites/shop-api \
  --application-resource /subscriptions/<sub>/resourceGroups/rg-shop-jobs/providers/Microsoft.Web/sites/shop-jobs \
  --evidence-backend /subscriptions/<sub>/resourceGroups/rg-shop-obs/providers/Microsoft.OperationalInsights/workspaces/law-shop-prod \
  --dedicated \
  --cross-owner-reviewed
```

Selection rules the preview enforces:

- Resources are added **individually**, across resource groups. Group siblings, parents,
  children, shared hosts, dependencies, and factory telemetry are never added implicitly.
- Evidence backends are selected **separately** from application membership with
  `--evidence-backend`. Selecting an application resource never authorizes a telemetry
  backend, and the same resource cannot serve as both.
- Shared resources or backends, staging/production mixing, cross-subscription or
  cross-tenant selections, and unsupported evidence routes are rejected. The preview never
  repairs configuration, diagnostics, queries, tags, or networking to make a selection pass.
- `--dedicated` and `--cross-owner-reviewed` record **your** review of this exact selection.
  An owner-scoped reservation is not a global technical lock across separate owner
  authorities, and the absence of DSF tags is not proof of dedication.
- Existing registration, claim, and tag signals are read conservatively. Conflicting,
  inconsistent, or unreadable state is not treated as unclaimed, and a matching name or tag
  never authorizes adoption. The preview writes no Azure tag.

Run it without `--tenant`, `--subscription`, `--app-environment`, or selections in an
interactive terminal to choose from discovered candidates. The equivalent complete command
is printed so the same plan can be replayed non-interactively. In a redirected or
non-interactive terminal, incomplete input fails explicitly: nothing is inferred, and a
generic yes flag is not selection authority.

## Read the plan

The plan is categorized so the different kinds of change stay distinguishable:

| Category | Meaning |
| --- | --- |
| preserved application/repository assets | untouched: configuration, diagnostics, tags, networking, delivery, operations, repository settings |
| newly owned factory resources | proposed factory-only resources, in the factory environment, not the application environment |
| reused owner/model prerequisites | existing owner App Configuration, Key Vault, and model deployments; never recreated |
| proposed grant scopes and factory identities | the exact role and scope a later apply would request, for identities that do not exist yet |
| owner registry claims | owner-scoped observation claims recorded in App Configuration, not Azure tags |
| allowed repository additions | only the `dsf:proposal` and `dsf-outcome:*` labels; no secrets, workflows, or protections |
| costs and omissions | continuing cost and what this step deliberately does not prove |
| blocked administrator prerequisites | who must act before onboarding can continue |

Planned identities have no principal ID yet: the actual binding must be checked before any
later grant write. Discovery or deployment rights are neither delegation nor effective
workload or network access.

Each plan carries a fingerprint over its decision-relevant inputs:

```text
[dsf] plan fingerprint: sha256:...
```

Pass that value back with `--expect-fingerprint` to prove you are acting on the plan you
reviewed. A changed selection fingerprints differently and is rejected as stale instead of
silently widening the earlier review.

## Outcomes

- `REVIEWABLE` (exit code 0): the exact selection is complete and nothing blocks review.
- `BLOCKED` (exit code 1): rejected selections or missing administrator prerequisites are
  listed individually, with the actor who must act next.

Evidence readiness, charter adoption, deliberation preview, and activation approval are
separate later steps. A reviewable boundary preview proves none of them.
