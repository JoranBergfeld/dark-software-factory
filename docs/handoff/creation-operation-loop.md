# Handoff packet: Build/Operate loop revision

Input packet for `/to-spec`. Produced by [Review of Build <> Operation loop](https://github.com/JoranBergfeld/dark-software-factory/issues/151),
synthesizing five closed decision tickets. Do not treat this as a spec: it is
the briefing `/to-spec` synthesizes from, not a finished Implementation
Decisions list.

## Scope

**In scope**: the Build (Creation) phase, the Operate (Operation) phase, the
implementations (tools) each phase runs on, and the handoff procedures that
connect them — maturity levels, security baseline, deploy-on-merge contract,
assignment/handoff mechanics.

**Out of scope**: Feature Council (Decide phase) integration and SRE-to-Council
learning — deferred to a separate future map. Direct implementation or code
changes — this packet feeds `/to-spec`, which produces the Implementation
Decisions; it does not itself change `docs/site/` or any code.

## Required terminology discipline

Every phase and every implementation (tool) must be named **strictly and
separately**: a phase name never doubles as a tool name, and vice versa. This
applies to the spec `/to-spec` produces, to the resulting code/config, and to
`docs/site/concept/*.md` and GitHub Pages.

- **Phase** = Build (Creation phase) or Operate (Operation phase). Decide
  (Feature Council) is out of scope here.
- **Implementation** = the concrete tool executing a phase: GitHub Cloud Agent
  (Build) or Azure SRE Agent (Operate).
- **Handoff** = the procedure connecting two phases (or a phase and
  production): a label-based issue handoff, an assignment mechanism, an
  incident-to-PR path.

### Known mismatches to fix

- `docs/site/concept/sre-agent.md` and `docs/site/concept/the-loop.md` use
  "SRE Agent" as the phase-level page title and heading — it is only the
  Operate phase's *implementation*, not the phase name. Canonical phase name
  is "Operation phase" (see `CONTEXT.md`).
- `docs/site/concept/creation.md` repeatedly says "GitHub Copilot Coding
  Agent" — canonical implementation name is "GitHub Cloud Agent" (`CONTEXT.md`
  says avoid the Copilot-Coding-Agent phrasing).
- `creation_maturity`/`operation_maturity` may remain as field/identifier
  names in code and config, but prose (docs, specs, comments) should say
  "maturity setting" per phase, not "maturity dial".

**Implementation Decision for `/to-spec` to include**: restructure
`docs/site/concept/*.md` (and the GitHub Pages they publish) into the
Phases / Implementations / Handoffs shape described above — at minimum,
splitting or renaming `creation.md` and `sre-agent.md` so each phase page is
named for its phase, with its implementation described as a distinct,
clearly-labeled part of that page, not the page's identity.

## Phases

### Build (Creation phase)

Turns accepted work issues into reviewed code changes. Governed by a
`creation_maturity` setting (low/medium/high), independent of the Operate
phase's setting.

- **low** (current default): `dsf-creation` ruleset requires 1 human approving
  review + green `ci`; no auto-merge.
- **medium**: auto-merge gated on Copilot review **approval** (not a human) +
  green `ci`. Requires GitHub's "Copilot approvals" preview + branch
  protection required-reviews + PR auto-merge, since Copilot review is
  comment-only by default and never blocks on its own.
- **high**: same Copilot-approval-gated auto-merge as medium, plus an agentic
  retry workflow — a GitHub Actions workflow triggers on a failed review
  (`pull_request_review` / `changes_requested`) or failed check, and
  re-invokes the GitHub Cloud Agent using a DSF-provisioned credential.

Merging to `main` at high `creation_maturity` starts the paved-road deployment
flow: automatic staging deployment, then automatic production promotion after
objective gates (green CI/security, staging success, smoke/health checks,
migration safety evidence, immutable release artifact). Deploy-on-merge keys
off `creation_maturity`, not `operation_maturity`.

Full detail: [Define Creation and Operation maturity levels](https://github.com/JoranBergfeld/dark-software-factory/issues/152),
[Decide production deployment-on-merge contract](https://github.com/JoranBergfeld/dark-software-factory/issues/156).

### Operate (Operation phase)

Observes production, responds to incidents, and can feed fix-forward work back
to the Build phase. Governed by an independent `operation_maturity` setting.
Deployment responsibility stays with the paved road, never with Operate; the
Operate phase only receives incidents and may trigger one bounded Recovery
Cloud Agent session, which must produce a Build-phase PR — it may not edit
production directly. Recovery is always fix-forward: the pipeline pauses at
the last healthy boundary and never auto-rolls-back; an operator's
break-glass process is the only route to a manual rollback.

- **low**: read-only monitoring RBAC (Reader + Monitoring Reader + Log
  Analytics Reader). Files incident issues with `creation:ready` + `incident`
  but requires a human to assign the GitHub Cloud Agent.
- **medium**: same read-only RBAC, but auto-assigns the GitHub Cloud Agent to
  the filed issue (via a PAT/user-to-server credential), removing the manual
  step.
- **high**: managed identity additionally gets Contributor on the product's
  resource group, enabling direct remediation (restart/scale/redeploy/config),
  likely excluding delete actions (open follow-up, noted below in Not yet
  specified).

Full detail: [Define Creation and Operation maturity levels](https://github.com/JoranBergfeld/dark-software-factory/issues/152),
[Decide production deployment-on-merge contract](https://github.com/JoranBergfeld/dark-software-factory/issues/156).

## Implementations

### GitHub Cloud Agent (Build phase implementation)

GitHub's managed coding agent (bot login `copilot-swe-agent`); takes assigned
issues, produces pull requests, runs under GitHub-managed identity. DSF holds
no credential that can push code directly — the Cloud Agent is the only
code-writing identity in Build.

Assignment can be automated by DSF/the SRE Agent, but re-invoking the agent
after a failed review/check (the high-maturity retry loop) requires a stored
user-to-server credential (PAT or GitHub App user token); `GITHUB_TOKEN` is
explicitly excluded, and server-to-server GitHub App installation tokens are
unsupported for triggering a cloud-agent session.

Full detail: [Research SRE Agent direct assignment to GitHub Cloud Agent](https://github.com/JoranBergfeld/dark-software-factory/issues/153),
[Research GitHub Cloud Agent and Copilot Review automation](https://github.com/JoranBergfeld/dark-software-factory/issues/154).

### Azure SRE Agent (Operate phase implementation)

Managed Azure product; DSF provisions it per product via
`infra/sre-agent.bicep`. Observes production telemetry, investigates
incidents, files fix-forward issues/PRs back into Build via the shared
`creation:ready` label. Its GitHub Connector can natively create issues with
an `assignees` field, but whether that reliably triggers a Cloud Agent session
the same way GitHub's documented `agentAssignment` mechanism does is an
undocumented gap (see Not yet specified).

Full detail: [Research SRE Agent direct assignment to GitHub Cloud Agent](https://github.com/JoranBergfeld/dark-software-factory/issues/153).

## Handoffs

- **Build → merge/deploy**: `dsf-creation` branch-protection ruleset, gated by
  `creation_maturity`; deploy-on-merge is a paved-road pipeline concern keyed
  off the same setting (see Build phase above).
- **Production → Operate**: Azure Monitor / Application Insights telemetry
  feeds the SRE Agent directly; no GitHub-side handoff involved.
- **Operate → Build (fix-forward)**: SRE Agent files an issue carrying
  `creation:ready` (+ `incident` for incident issues) — the same label Build
  already watches, so a production incident and planned work enter Build the
  same way. Assignment to the GitHub Cloud Agent is manual at low
  `operation_maturity`, automatic at medium/high (credential caveat above).
- **Failed deployment → Operate**: a paved-road deploy/staging failure creates
  an Operation incident with correlated immutable release, deployment, PR, and
  agent-session identities, and may trigger exactly one bounded Recovery Cloud
  Agent session producing a Build-phase PR.

## Paved-road security baseline

Applies to every provisioned product regardless of maturity: Advanced
Security where supported, CodeQL/code scanning, secret scanning with push
protection, dependency review, Dependabot security/version maintenance.
Provisioning rejects any repo/plan that can't enforce this baseline rather
than silently downgrading it.

All maturity levels require green CI and security checks. Review enforcement
varies by `creation_maturity` (see Build phase above). Merge-blocking
thresholds: new high-severity/high-precision CodeQL alerts, any exposed
secret, critical/high dependency vulnerabilities; lower-severity findings are
tracked, not blocking. Rulesets are restrictive: DSF automation may bypass
only for provisioning/configuration; product merges (including Cloud
Agent-created PRs) cannot bypass CI, security checks, or the maturity-specific
review gate. Break-glass requires a named operator, explicit reason, short
expiry, and an auditable record.

Full detail: [Decide paved-road security baseline](https://github.com/JoranBergfeld/dark-software-factory/issues/155).

## Reference scenario note

`sre-agent-workshop/scenarios/cloud-agent-handover` is inspiration, not a
binding source — it's a living artifact prone to change. One concrete
divergence worth carrying forward: the workshop scenario deliberately files
its incident issue **unassigned**, requiring a human to assign the Cloud Agent
as a governance gate. DSF's medium/high `operation_maturity` levels instead
automate that assignment. This is a deliberate choice, not an oversight.

## Not yet specified (carried from the map's fog)

- Exact Azure role/action-level scoping for SRE Agent high-maturity
  remediation (full Contributor vs. a narrower custom role excluding delete).
- Whether the SRE Agent's GitHub Connector reliably triggers a Cloud Agent
  session the same way the documented `agentAssignment` mechanism does, or
  only sets a plain `assignees` field (undocumented by Microsoft).

## Out of scope (do not resolve here)

- Feature Council (Decide phase) integration and SRE-to-Council learning —
  revisit in a later, separate map.
