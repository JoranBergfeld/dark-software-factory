# Coding Squad

> Build it. The Squad picks up the Council's issues, writes the software, and
> opens pull requests, backed by a knowledge base that grows as it works.

## Why this phase

Deciding what to build is only half the job. Something has to build it. The
Coding Squad is that half. It takes the issues the Council filed and turns them
into working code and pull requests, without a person assigning the work by
hand. It also remembers: as it ships, it writes down what it learned, so the
next issue starts from more context than the last one.

DSF does not implement the Squad itself. It uses
[`bradygaster/squad`](https://github.com/bradygaster/squad), an existing
coding-squad product, and wires it into the product's repo. The factory's job
here is integration and handoff, not building another coding agent.

## Responsibilities

- Watch the product repo for issues that carry the handoff label.
- Triage each one and dispatch the Copilot coding agent to implement it.
- Open a pull request with the change.
- Persist what the work taught it into the `.squad/` knowledge base, so future
  triage is better grounded.

The Squad lives in the product repo, next to the code it changes. Its state
(`.squad/`) is part of that repo, which keeps the coding context and the code in
the same place.

## Inputs and outputs

**In:** GitHub issues carrying `squad:ready`. Most come from the Feature
Council. Incident issues from the SRE Agent carry the same label and enter the
same way.

**Out:** pull requests against the product repo. From there the normal review
and merge path takes over and the change deploys.

## Handoffs

Upstream, the Squad takes from two sources, both through the same label: the
Feature Council (new work) and the SRE Agent (fix-forward incidents). It does
not care which one filed the issue. The label is the whole contract.

Downstream, the Squad produces pull requests. What happens to a PR (review,
merge, deploy) is the boundary where a person can still step in, and it is where
production starts to change, which is what the SRE Agent then watches.

## Harness and steering

- The handoff label (`squad:ready`) is the single wire between intake and the
  Squad. Change nothing else and issues still flow.
- `squad triage --execute --label squad:ready` is how the factory points the
  Squad at the right issues.
- Copilot auto-assignment is enabled at provisioning
  (`squad copilot --auto-assign`) so triaged issues reach the coding agent
  without a human handoff.
- The `.squad/` preset and knowledge base shape how the Squad triages and what
  it remembers.

DSF sets these up when it stamps the instance (the `squad_init`,
`squad_copilot`, and `squad_triage` provisioning steps). After that the loop
runs on the label.

## Where it lives and how autonomous it is today

The Squad is an external product that lives inside the product's own repository,
not in this one. DSF provisions and configures it through the CLI but does not
own its code. The handoff into it is implemented and tested in this repo
(ADR 0007). The coding agent and the knowledge loop are the Squad product's own.
In factory terms this phase is an integration: the wiring is in place, and how
far the coding agent runs on its own follows the Squad product and the repo's
own review and merge settings.

**See also:** the [loop overview](../../README.md#the-loop), the upstream
[Feature Council](feature-council.md), and the [SRE Agent](sre-agent.md) that
watches what ships.
