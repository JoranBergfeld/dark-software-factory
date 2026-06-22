# SRE Agent

> Operate and feed back. The SRE Agent watches the product in production, turns
> incidents into fixes by filing them back into the Squad, and feeds what it
> learns to the Council.

## Why this phase

A product that ships is not done. It runs, and running surfaces things no plan
predicted: regressions, outages, slow degradations. The SRE Agent is the part of
the factory that lives with the product in production. When something breaks, it
does not just alert. It investigates and files the fix as an issue back into the
same intake the Squad already watches, so the break becomes the next pull
request. It also closes the loop: what production teaches goes back to the
Council as new signal, so the factory learns how the product actually behaves,
not only how it was meant to.

DSF does not build this agent either. It uses the managed **Azure SRE Agent**
product (ADR 0009, superseded by ADR 0015). The factory's job is to provision it
per product and keep it pointed at the same handoff the rest of the loop uses.

## Responsibilities

- Observe production telemetry (Azure Monitor and Application Insights) for the
  product.
- Investigate incidents rather than only reporting them.
- Fix-forward: file an issue or pull request for the fix, carrying the handoff
  label, so the Squad picks it up.
- Feed operational signals and lessons back to the Feature Council: the SRE
  Agent stamps `incident`, and the council's `incidents` and `azuremonitor`
  sources pull incidents and telemetry on the council's schedule (ADR 0013).

## Inputs and outputs

**In:** production telemetry for the product's Azure resources.

**Out:** incident issues and pull requests in the product repo, carrying
`squad:ready` and, for incident issues, `incident`. The fast path sends incidents
to the Squad; the council's `incidents` and `azuremonitor` sources also pull
incidents and telemetry on the council's schedule (ADR 0013).

## Handoffs

Upstream, the SRE Agent takes from production itself: the running system is its
input.

Downstream, it hands to the Coding Squad through the same `squad:ready` label
the Council uses, so a production incident and a planned feature reach the Squad
the same way and need no separate path. The second downstream hand, back to the
Feature Council as signal, uses the `incident` label plus the council's
`incidents` and `azuremonitor` sources (ADR 0013), so recurring incidents become
systemic hardening proposals.

## Harness and steering

- `dsf new` provisions the agent via `infra/sre-agent.bicep` (subscription-scoped)
  as a real `deploy_sre_agent` step. No interactive wizard. No OAuth flow.
- The Bicep creates a dedicated resource group (`rg-dsf-sre-<product>`) in a
  supported region (Sweden Central by default, the only EU option among the three
  the agent supports), a user-assigned managed identity for the agent, and the
  `Microsoft.App/agents` resource itself.
- The agent's managed identity gets Reader + Monitoring Reader + Log Analytics Reader
  on the factory resource group and any extra monitored-app resource groups. It also
  gets Monitoring Contributor at subscription scope for alert lifecycle management.
  Azure Monitor connectors (Log Analytics + App Insights) are wired as ARM
  sub-resources so the Azure Monitor link is explicit and verifiable.
- The product GitHub repo is connected via a best-effort Phase-2 data-plane call
  (`az rest`). If the token or endpoint is unavailable at provision time, the step
  records a skip note and the operator can connect manually. The agent comes up
  Azure-Monitor-connected either way.
- Provisioning requires the caller to hold Owner, or Contributor + User Access
  Administrator, on the subscription.
- The handoff label is the same `squad:ready` the rest of the loop uses, created
  already by the `create_labels` provisioning step.

## Where it lives and how autonomous it is today

The SRE Agent is a managed Azure product, provisioned once per product against
that product's resource group and repo. It is not code in this repository. What
DSF provides is the Bicep (`infra/sre-agent.bicep`) and the `deploy_sre_agent`
provisioner step that runs it. After provisioning, `dsf new` writes a short
`sre-agent.md` summary (what got created, the agent portal link, a one-time
`what-if`/verify note). The fix-forward handoff into the Squad uses the shared
`squad:ready` label. The feedback path into the Council is built through the
`incident` marker plus the `incidents` and `azuremonitor` sources (ADR 0013; ADR
0015 supersedes ADR 0009's render-only approach).

**See also:** the [loop overview](the-loop.md), the
[Coding Squad](coding-squad.md) it fixes forward into, and the
[Feature Council](feature-council.md) it feeds.
