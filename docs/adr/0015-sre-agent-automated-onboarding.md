# ADR 0015: Provision the Azure SRE Agent via Bicep (automated onboarding)

- Status: Accepted
- Date: 2026-06-21
- Supersedes: ADR 0009 (render-only "no headless path" onboarding)
- Relates to: ADR 0004 (ACA runtime), ADR 0006 (Azure data adapters), ADR 0013 (SRE/council feedback)

## Context

ADR 0009 (June 18) concluded that headless onboarding of the Azure SRE Agent was not
achievable: the agent could only be set up through a wizard at `sre.azure.com` with
browser-OAuth GitHub connection and interactive Azure resource-access grants. So the
`onboard_sre_agent` provisioner step rendered a manual runbook and did nothing else.

That premise is now obsolete. Microsoft ships `Microsoft.App/agents` as a proper ARM
resource, deployable via Bicep templates (see `microsoft/sre-agent` on GitHub) and backed by
a two-plane REST API (ARM for infrastructure, a per-agent data-plane for repo/webhook
connections). DSF can provision the agent fully in Phase 1 and connect the product GitHub
repo in Phase 2, both through the provisioner's existing injected `run` callable.

## Decision

Replace `onboard_sre_agent` with `deploy_sre_agent`, a real provisioning step with two phases:

**Phase 1 (ARM via Bicep).** A new subscription-scoped template `infra/sre-agent.bicep`
declares:
- A dedicated resource group for the agent (`rg-dsf-sre-<product>`), in a supported region.
- A user-assigned managed identity (UAMI) bound to the `Microsoft.App/agents` resource.
- Cross-RG role assignments via a per-RG module (`infra/modules/sre-rg-roles.bicep`):
  Reader + Monitoring Reader + Log Analytics Reader on each monitored resource group (the
  factory RG and any extra product RGs specified in `monitored_resource_groups`).
- Monitoring Contributor at subscription scope (alert lifecycle management).
- Log Analytics + Application Insights connectors (ARM sub-resources) pointed at the
  factory's workspace/component.

The step runs `az deployment sub create -l <sreAgentLocation> -f infra/sre-agent.bicep -p ...`
and gets `appInsightsId` / `logAnalyticsId` from the outputs already captured by the
preceding `provision_azure` step.

**Phase 2 (best-effort repo connect).** After the ARM deploy returns `agentEndpoint`, the
step POSTs the product GitHub repo via `az rest` (audience `https://azuresre.dev`). This
uses the GitHub token already in Key Vault (the same one `_seed_github_token` handles). If
the endpoint or token is missing, the step records what it skipped and succeeds anyway: the
agent is usable for Azure Monitor without the repo connection, and the manual follow-up is
documented in the step output.

`execute=False` stays side-effect-free: planned commands are recorded, the manifest is
written, no `az` calls run.

## Region constraint

The `Microsoft.App/agents` resource is only available in some regions. The live provider
list (2026-06) is Sweden Central, UK South, East US 2, Australia East, France Central,
Canada Central, and Korea Central (verify with `az provider show --namespace Microsoft.App`,
since the set grows). `InstanceSpec.sre_agent_location` (default `swedencentral`) is
validated against this set at spec-construction time. Sweden Central is the EU-data-residency
default. Once deployed, the agent can monitor resource
groups in any region.

## Subscription-level RBAC prerequisite

Creating the agent and assigning roles across resource groups requires the caller to hold
Owner, or Contributor + User Access Administrator, on the subscription. If the deploy fails
on a role assignment, the Azure error surface is passed through unchanged and the documented
prerequisite is the fix. DSF does not try to grant these roles itself.

## Schema caveats

The `Microsoft.App/agents` ARM resource type is new and not yet stable. The Bicep in
`infra/modules/sre-agent-resources.bicep` carries `TODO(confirm)` markers on the
api-version, the model provider config shape, the connector sub-resource type casing, and
`properties.agentEndpoint`. These are to be verified against a live deploy and the
`microsoft/sre-agent` reference templates before the first real `dsf new --execute` run.
Phase 2's data-plane endpoint path also carries a `TODO(confirm)`.

## Consequences

- The interactive wizard + OAuth path is gone. Onboarding is now a provisioner step like
  any other.
- The `sre-onboarding.md` runbook shrinks to a short post-deploy summary (`sre-agent.md`):
  what got created, the agent portal link, and the one-time `what-if`/verify note.
- The handoff contract is unchanged: the agent files issues/PRs with `squad:ready` and
  `incident`, and the council's `incidents` and `azuremonitor` sources pull from those
  (ADR 0013). Nothing on the consumer side changes.
- If Phase 2 fails (unstable data-plane path, missing token), the provision still completes
  and the operator gets a clear skip note. The agent comes up Azure-Monitor-connected.
- The subscription-level RBAC requirement is a real operational dependency. It is now
  explicit rather than hidden by a render-only step.
- ADR 0009 is marked Superseded.
