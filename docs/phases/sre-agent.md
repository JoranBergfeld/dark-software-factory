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
product (ADR 0009). The factory's job is to onboard it per product and keep it
pointed at the same handoff the rest of the loop uses.

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

- The agent is onboarded interactively per product (wizard plus OAuth) at
  sre.azure.com. `dsf new` renders a per-product runbook (`sre-onboarding.md`)
  with the exact resource group, region, and repo to use.
- It connects to the product repo over OAuth or a token and files into it.
- It is granted Reader on the product's dedicated resource group, which scopes
  what it can see to one product.
- The handoff label is the same `squad:ready` the rest of the loop uses, created
  already by the `create_labels` provisioning step.

## Where it lives and how autonomous it is today

The SRE Agent is a managed Azure product, onboarded once per product against
that product's resource group and repo. It is not code in this repository. What
DSF provides is the per-product onboarding runbook, rendered by the
`onboard_sre_agent` provisioning step (render only, no Azure calls). The
fix-forward handoff into the Squad is defined and uses the shared label. The
feedback path into the Council is built through the `incident` marker plus the
`incidents` and `azuremonitor` sources (ADR 0013; ADR 0009 supersedes the
earlier bespoke design in ADR 0008).

**See also:** the [loop overview](../../README.md#the-loop), the
[Coding Squad](coding-squad.md) it fixes forward into, and the
[Feature Council](feature-council.md) it feeds.
