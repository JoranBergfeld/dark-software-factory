# Operation phase

> Operate and feed back. The Operation phase watches the product in
> production, turns incidents into fixes by filing them back into the
> Creation phase, and feeds what it learns to the Council.

## Why this phase

A product that ships is not done. It runs, and running surfaces things no plan
predicted: regressions, outages, slow degradations. The Operation phase is the
part of the factory that lives with the product in production. When something
breaks, it does not just alert. It investigates and files the fix as an issue
back into the same intake the Creation phase already watches, so the break
becomes the next pull request. It also closes the loop: what production
teaches goes back to the Council as new signal, so the factory learns how the
product actually behaves, not only how it was meant to.

DSF does not build the agent that does this either — see
[Implementation: Azure SRE Agent](#implementation-azure-sre-agent) below. The
factory's job is to provision it per product and keep it pointed at the same
handoff the rest of the loop uses.

## Responsibilities

- Observe production telemetry (Azure Monitor and Application Insights) for the
  product.
- Investigate incidents rather than only reporting them.
- Fix-forward: file an issue or pull request for the fix, carrying the handoff
  label, so the Creation phase picks it up — see [Handoffs](handoffs.md).
- Feed operational signals and lessons back to the Feature Council: the
  Operation phase stamps `incident`, and the council's `incidents` and
  `azuremonitor` sources pull incidents and telemetry on the council's
  schedule (ADR 0013).

## Inputs and outputs

**In:** production telemetry for the product's Azure resources.

**Out:** incident issues and pull requests in the product repo, carrying
`creation:ready` and, for incident issues, `incident`. The fast path sends
incidents to the Creation phase; the council's `incidents` and `azuremonitor`
sources also pull incidents and telemetry on the council's schedule (ADR 0013).
See [Handoffs](handoffs.md) for the full mechanism.

## Implementation: Azure SRE Agent

The Operation phase's implementation today is the managed **Azure SRE Agent**
product (ADR 0009, superseded by ADR 0015). This is an implementation detail
of the phase, not the phase's name: DSF could point the same contract
(watch production, fix-forward through the shared label, feed signal back to
the Council) at a different implementation without changing what the
Operation phase is.

- `dsf new` provisions the agent via `infra/sre-agent.bicep`
  (subscription-scoped) as a real `deploy_sre_agent` step. No interactive
  wizard. No OAuth flow.
- The Bicep creates a dedicated resource group (`rg-dsf-sre-<product>`) in a
  supported region (Sweden Central by default, the only EU option among the
  three the agent supports), a user-assigned managed identity for the agent,
  and the `Microsoft.App/agents` resource itself.
- The agent's managed identity gets Reader + Monitoring Reader + Log Analytics
  Reader on the factory resource group and any extra monitored-app resource
  groups at every operation maturity. It also gets Monitoring Contributor at
  subscription scope for alert lifecycle management. At `high` operation
  maturity, it additionally gets a remediation-capable custom role (all
  actions except delete) on each monitored resource group. Azure Monitor
  connectors (Log Analytics + App Insights) are wired as ARM sub-resources so
  the Azure Monitor link is explicit and verifiable.
- The human owner/governance principal gets Reader + SRE Agent Administrator on
  the agent's resource group so they can open and operate the agent UI after
  `dsf new`.
- Provisioning requires the caller to hold Owner, or Contributor + User Access
  Administrator, on the subscription.
- The handoff label is the same `creation:ready` the rest of the loop uses,
  created already by the `create_labels` provisioning step.

## Where it lives and how autonomous it is today

The Azure SRE Agent is a managed Azure product, provisioned once per product
against that product's resource group and repo. It is not code in this
repository. What DSF provides is the Bicep (`infra/sre-agent.bicep`) and the
`deploy_sre_agent` provisioner step that runs it. After provisioning, `dsf new`
writes a short `sre-agent.md` summary (what got created, the agent portal
link, a one-time `what-if`/verify note). The fix-forward handoff into the
Creation phase uses the shared `creation:ready` label. The feedback path into
the Council is built through the `incident` marker plus the `incidents` and
`azuremonitor` sources (ADR 0013; ADR 0015 supersedes ADR 0009's render-only
approach).

Autonomy is the `operation_maturity` setting. Low and medium keep the agent on
the read-only Reader surface; high additionally grants it a remediation role
so it can act, not just observe, on the resource groups it monitors.

**See also:** the [loop overview](the-loop.md), [Handoffs](handoffs.md), the
[Creation phase](creation.md) it fixes forward into, and the
[Feature Council](feature-council.md) it feeds.
