# Azure SRE Agent direct assignment to GitHub Cloud Agent

**Research ticket:** [#153](https://github.com/JoranBergfeld/dark-software-factory/issues/153)
**Parent map:** [#151](https://github.com/JoranBergfeld/dark-software-factory/issues/151)
**Current through:** 2026-09-04

## Question

Can the Azure SRE Agent file incident issues and directly assign them to the GitHub Cloud Agent
(formerly GitHub Copilot coding agent), including required GitHub configuration, permissions, and
known limitations? How close is this to the `sre-agent-workshop` `scenarios/cloud-agent-handover`
shape?

## Executive summary / direct answer

**Partially yes, with a mandatory human-in-the-loop gate in the currently documented pattern.**
The Azure SRE Agent can natively create GitHub issues via its built-in GitHub Connector, including
issues with an `assignees` field. GitHub Copilot cloud agent (bot login `copilot-swe-agent`) can be
triggered by assigning an issue to that login via the GitHub REST or GraphQL API. However:

1. The Azure SRE Agent's documented default pattern — including the official workshop — deliberately
   creates the issue **unassigned** and requires a human operator to assign `copilot-swe-agent` as a
   reviewed gate.
2. Whether the SRE Agent's GitHub Connector uses the special `agentAssignment` GraphQL/REST input
   (the mechanism GitHub documents for reliably triggering a cloud-agent session with custom
   instructions) versus a plain `assignees` array is **not documented by Microsoft** — this is the
   central open gap.
3. The GitHub API path for triggering Copilot cloud agent sessions requires **user-to-server tokens**
   (PAT or OAuth) and explicitly excludes server-to-server GitHub App installation tokens — a
   constraint that directly affects the SRE Agent's "BYO GitHub App" auth mode.

Net: end-to-end automatic SRE-Agent-files-issue → Cloud-Agent-starts-work is **technically plausible
but not demonstrated or governed in any primary source**; every primary source that shows a working
pipeline (the workshop) inserts a human approval and a human assignment step by design.

## Facts that constrain the decision

### Azure SRE Agent GitHub Connector

- Azure SRE Agent has three distinct GitHub integration modes that can coexist: **Code Access**
  (read-only source), **GitHub Connector** (issue/PR/workflow operations), and **GitHub MCP** (full
  catalog via MCP). Each requires separate configuration.
  [Azure SRE Agent: GitHub connector overview](https://learn.microsoft.com/en-us/azure/sre-agent/github-connector)
- The GitHub Connector explicitly supports **"Create issues with title, body, labels, and
  assignees"** natively — no webhook or custom code required.
  [Azure SRE Agent: GitHub connector overview](https://learn.microsoft.com/en-us/azure/sre-agent/github-connector)
  (section "Issue and pull request management")
- Authentication options for the GitHub Connector: **OAuth** (interactive, auto-refreshing ~6
  months), **PAT**, or **BYO GitHub App** (private key in Azure Key Vault). Only BYO GitHub App
  supports GitHub Enterprise Cloud (`<tenant>.ghe.com`).
  [Azure SRE Agent: GitHub connector overview](https://learn.microsoft.com/en-us/azure/sre-agent/github-connector)
  (section "Authentication methods")
- Required permissions for issue creation via OAuth/PAT: an issue-capable repo scope — the classic
  PAT `repo` scope, or for fine-grained PATs, repository Issues: Read/Write.
  [Azure SRE Agent: GitHub connector overview](https://learn.microsoft.com/en-us/azure/sre-agent/github-connector)
  (section "Permissions by auth type")
- The SRE Agent response plan can be set to **Review** (proposed actions need explicit operator
  approval) or **Autonomous** (actions taken automatically). The workshop scenario uses Review mode.
  [Azure SRE Agent: Automate incidents tutorial](https://learn.microsoft.com/en-us/azure/sre-agent/automate-incidents)
  (section "Create an incident response plan")
- **No Microsoft documentation found** describing a specific integration path from Azure SRE Agent
  to Copilot cloud agent that uses the `agentAssignment` GraphQL/REST input or any special
  triggering mechanism. This is an open gap in Microsoft's published documentation.

### GitHub Copilot cloud agent (formerly coding agent)

- The product is officially called **"GitHub Copilot cloud agent"**; the bot login for assignment
  is `copilot-swe-agent`, shown in the UI as "Copilot."
  [GitHub: About GitHub Copilot cloud agent](https://docs.github.com/en/copilot/concepts/agents/cloud-agent/about-cloud-agent)
- Confirmed triggers for a cloud agent session: assigning an issue to `copilot-swe-agent` via the
  GitHub UI; GraphQL `createIssue`/`updateIssue`/`addAssigneesToAssignable`/
  `replaceActorsForAssignable` mutations with the `agentAssignment` input (requires
  `GraphQL-Features: issues_copilot_assignment_api_support,coding_agent_model_selection` header,
  **public preview**); `POST /agents/repos/{owner}/{repo}/tasks` REST API; Copilot Automations on
  "issue created"; GitHub CLI, IDEs, Slack, Teams, Azure Boards, Jira, Linear, Raycast.
  [GitHub: Using Copilot cloud agent via the API](https://docs.github.com/en/copilot/how-tos/use-copilot-agents/cloud-agent/use-cloud-agent-via-the-api)
- **Critical token constraint**: the agent tasks API and the `agentAssignment` GraphQL mutations
  require **user-to-server tokens** (PAT, OAuth app token, or GitHub App user-to-server token).
  **Server-to-server GitHub App installation tokens are explicitly not supported.**
  [GitHub: Using Copilot cloud agent via the API](https://docs.github.com/en/copilot/how-tos/use-copilot-agents/cloud-agent/use-cloud-agent-via-the-api)
  (section "Authentication")
- Docs state both REST and GraphQL issue APIs "support an optional Agent Assignment input to
  customize the task," implying a plain REST `assignees: ["copilot-swe-agent"]` call may also
  trigger the agent — but this is **not explicitly confirmed** as equivalent to the documented
  `agentAssignment` preview path.
  [GitHub: Using Copilot cloud agent via the API](https://docs.github.com/en/copilot/how-tos/use-copilot-agents/cloud-agent/use-cloud-agent-via-the-api)
- Copilot cloud agent works in **one repository per session**, opens **one PR per task**, and has a
  **59-minute hard session timeout**.
  [GitHub: About GitHub Copilot cloud agent](https://docs.github.com/en/copilot/concepts/agents/cloud-agent/about-cloud-agent)
  (section "Limitations")
- Copilot **Automations** ("When an issue is created") are an alternative trigger, but are only
  available in **private or internal repositories** and require Copilot Business/Enterprise/Pro/
  Pro+/Max.
  [GitHub: About Copilot automations](https://docs.github.com/en/copilot/concepts/agents/cloud-agent/about-automations)

## Required GitHub configuration

- **Verify `copilot-swe-agent` availability** with
  `suggestedActors(capabilities: [CAN_BE_ASSIGNED])` on the repository before relying on assignment;
  if absent, cloud agent is not enabled/eligible for that repo. Exact query used by the workshop:
  ```bash
  gh api graphql \
    -f query='query($owner:String!,$name:String!){repository(owner:$owner,name:$name){suggestedActors(capabilities:[CAN_BE_ASSIGNED],first:100){nodes{login}}}}' \
    -f owner="$OWNER" -f name="$NAME" --jq '.data.repository.suggestedActors.nodes[].login'
  ```
  [sre-agent-workshop: 04-configure-incident-response.md](https://github.com/JoranBergfeld/sre-agent-workshop/blob/main/scenarios/cloud-agent-handover/docs/04-configure-incident-response.md)
- **Licensing**: Copilot Business/Enterprise subscribers require an administrator to explicitly
  enable the Copilot cloud agent policy at org (and optionally enterprise) level — disabled by
  default. Pro/Pro+/Max have it enabled by default.
  [GitHub: Managing access to GitHub Copilot cloud agent](https://docs.github.com/en/copilot/concepts/agents/cloud-agent/access-management)
- **Repository opt-out check**: org admins/repo owners can exclude individual repos from Copilot
  cloud agent; confirm the target repo is not excluded.
  [GitHub: Managing access to GitHub Copilot cloud agent](https://docs.github.com/en/copilot/concepts/agents/cloud-agent/access-management)
- **Fine-grained PAT scopes**: Read access to metadata; Read/write to actions, contents, issues, and
  pull requests. Classic PAT: `repo` scope.
  [GitHub: Using Copilot cloud agent via the API](https://docs.github.com/en/copilot/how-tos/use-copilot-agents/cloud-agent/use-cloud-agent-via-the-api)
- **GraphQL preview header**: `GraphQL-Features: issues_copilot_assignment_api_support,coding_agent_model_selection`
  required when using the `agentAssignment` GraphQL input.
  [GitHub: Using Copilot cloud agent via the API](https://docs.github.com/en/copilot/how-tos/use-copilot-agents/cloud-agent/use-cloud-agent-via-the-api)
- **Azure SRE Agent connector setup**: configure the GitHub Connector (Builder > Connectors > Add
  connector > GitHub) authenticated with OAuth or PAT — **not** a GitHub App installation token if
  the intent is to trigger Copilot cloud agent sessions.
  [Azure SRE Agent: Set up GitHub connector](https://learn.microsoft.com/en-us/azure/sre-agent/setup-github-connector)
- **Optional `copilot-setup-steps.yml`** in the target repository to customize the cloud agent's
  ephemeral environment.
  [GitHub: About GitHub Copilot cloud agent](https://docs.github.com/en/copilot/concepts/agents/cloud-agent/about-cloud-agent)
  (section "Customizing Copilot cloud agent")

## Known limitations / open gaps

1. **`agentAssignment` is public preview** for both GraphQL and REST paths — subject to change.
   [GitHub: Using Copilot cloud agent via the API](https://docs.github.com/en/copilot/how-tos/use-copilot-agents/cloud-agent/use-cloud-agent-via-the-api)
2. **Server-to-server token block**: if the Azure SRE Agent's BYO GitHub App is configured as an
   installation (server-to-server), it cannot trigger Copilot cloud agent sessions — only
   user-to-server OAuth/PAT tokens work. This is a critical incompatibility with the BYO App auth
   mode most likely to be used for unattended/production SRE Agent deployments.
3. **No documented SRE Agent → Copilot trigger path**: Microsoft confirms SRE Agent can create
   issues with assignees but does not document whether its connector uses the special GraphQL path
   needed to reliably trigger a Copilot session. **This is the central unverified claim** blocking a
   confident "yes."
4. **Plain REST assignees may not reliably trigger** an agent session identically to the documented
   `agentAssignment` preview path — not confirmed in primary docs either way.
5. **Automations require private/internal repos only** — public repos are excluded from the
   "issue created" trigger alternative.
   [GitHub: About Copilot automations](https://docs.github.com/en/copilot/concepts/agents/cloud-agent/about-automations)
6. **59-minute hard timeout per session**; complex incident remediations may not complete in time.
7. **One repository per session** — cloud agent cannot span multiple repos in one run.
8. **Branch protection rules/rulesets may block cloud agent access** entirely in some
   configurations.
9. **No label-based trigger documented** — GitHub's primary triggers are assignment or the agents
   API, not labels; the closest label-like equivalent is a Copilot Automation with a search-query
   filter.

## Comparison to `sre-agent-workshop/scenarios/cloud-agent-handover`

### What the scenario actually implements

The scenario is a workshop learning exercise, not a fully automated production pipeline.

- **Incident**: a Blazor App Service ships `POST /api/feature` throwing `NotImplementedException`.
  Triggering it sends failed requests that fire the Azure Monitor alert `unfinished-feature-5xx`
  (Sev2, >3 failures in 5 minutes).
  [scenario.yaml](https://github.com/JoranBergfeld/sre-agent-workshop/blob/main/scenarios/cloud-agent-handover/scenario.yaml),
  [README.md](https://github.com/JoranBergfeld/sre-agent-workshop/blob/main/scenarios/cloud-agent-handover/README.md)
- **Flow**: Azure Monitor fires → SRE Agent (Review-mode response plan
  `cloud-agent-handover-review`) correlates the alert with source/telemetry → asks the operator for
  explicit approval → on approval, creates a GitHub issue **without an assignee** → operator reviews
  and **manually assigns `copilot-swe-agent`** via the GitHub UI → Copilot cloud agent opens a PR →
  CI runs (app validation + CodeQL) → operator reviews/merges → operator deploys manually.
- **Explicit governance contract**, from the scenario's own knowledge file:
  > "After approval, create exactly one issue without an assignee. The learner reviews the created
  > issue, then assigns `copilot-swe-agent`. The SRE Agent must not create a branch or pull request,
  > merge changes, or deploy the application."
  [knowledge/operational-guidelines.md](https://github.com/JoranBergfeld/sre-agent-workshop/blob/main/scenarios/cloud-agent-handover/knowledge/operational-guidelines.md)
- **Fallback path** (no Azure infra available): the learner manually opens a GitHub issue from
  `sample-issue.md`, submits it unassigned, then manually assigns `copilot-swe-agent`. Docs
  explicitly instruct not to claim Azure telemetry was collected in this path.
  [README.md](https://github.com/JoranBergfeld/sre-agent-workshop/blob/main/scenarios/cloud-agent-handover/README.md)

### Divergence from current native capabilities

| Dimension | Workshop scenario | Native capability gap |
|---|---|---|
| Issue assignment | Human manually assigns `copilot-swe-agent` after review | SRE Agent connector *can* include assignees at creation time, but no documented workflow does this automatically |
| Automation level | Deliberately human-gated (Review mode) | Autonomous mode + `copilot-swe-agent` assignee is technically possible but undocumented and ungoverned |
| `agentAssignment` API | Not used — human UI assignment triggers the agent | GitHub's documented programmatic trigger path needs the GraphQL preview feature with a special header |
| Token type | Human's own GitHub session (user-to-server) | SRE Agent BYO GitHub App uses an installation token (server-to-server), which cannot trigger cloud agent sessions |
| Intent | The human-in-the-loop approval *is* the point of the workshop | A "fully automated" variant would remove the governance step the workshop exists to teach |
| Deployment | Operator runs `deploy.sh`/`deploy.ps1` manually after merge | No automated deploy from SRE Agent; Copilot cloud agent cannot deploy to Azure |

### Where the workshop is ahead of documented Azure SRE Agent capability descriptions

- Uses a **knowledge file** (`operational-guidelines.md`) uploaded to the SRE Agent's knowledge base
  to explicitly govern handover behavior — a real, documented SRE Agent feature (Builder →
  Knowledge base).
  [docs/03-onboard-sre-agent.md](https://github.com/JoranBergfeld/sre-agent-workshop/blob/main/scenarios/cloud-agent-handover/docs/03-onboard-sre-agent.md)
- Uses **both** the GitHub Code Access connector (source correlation) **and** the GitHub Connector
  (issue creation) as two separate configurations, mirroring the real product's distinct
  integration modes.
  [docs/04-configure-incident-response.md](https://github.com/JoranBergfeld/sre-agent-workshop/blob/main/scenarios/cloud-agent-handover/docs/04-configure-incident-response.md)

## Implications for the DSF build↔operate loop (informational, not a decision)

This research does not recommend an implementation; it surfaces facts for `/to-spec`:

- If DSF wants an SRE-Agent-style Operation-phase actor to hand incidents to GitHub Cloud Agent,
  the only primary-source-validated pattern today keeps a human assignment step between issue
  creation and cloud agent start.
- Any design assuming direct, unattended API-driven assignment should flag the two unresolved
  points above (connector's internal API path; server-to-server token exclusion) as risks requiring
  either GitHub/Microsoft clarification or a spike, not settled facts.

## Full source list

| Source | URL | Type | Supports |
|---|---|---|---|
| Azure SRE Agent documentation landing page | https://learn.microsoft.com/en-us/azure/sre-agent/ | Primary | Overview of SRE Agent structure, available docs |
| Azure SRE Agent: GitHub connector overview | https://learn.microsoft.com/en-us/azure/sre-agent/github-connector | Primary | SRE Agent can create issues with assignees; connector types; auth methods; permission tables |
| Azure SRE Agent: Set up GitHub connector (OAuth/PAT) | https://learn.microsoft.com/en-us/azure/sre-agent/setup-github-connector | Primary | Connector setup steps; OAuth/PAT options; permission tables |
| Azure SRE Agent: Automate incidents tutorial | https://learn.microsoft.com/en-us/azure/sre-agent/automate-incidents | Primary | Response plan creation; Review vs. Autonomous autonomy; Azure Monitor integration |
| GitHub: About GitHub Copilot cloud agent | https://docs.github.com/en/copilot/concepts/agents/cloud-agent/about-cloud-agent | Primary | Triggers; limitations (59-min, single repo); licensing; entrypoints |
| GitHub: About Copilot automations | https://docs.github.com/en/copilot/concepts/agents/cloud-agent/about-automations | Primary | "Issue created" automation trigger; private-repo restriction; plans |
| GitHub: Managing access to Copilot cloud agent | https://docs.github.com/en/copilot/concepts/agents/cloud-agent/access-management | Primary | Org/enterprise policy defaults by plan; repo opt-out |
| GitHub: Using Copilot cloud agent via the API | https://docs.github.com/en/copilot/how-tos/use-copilot-agents/cloud-agent/use-cloud-agent-via-the-api | Primary | Agent tasks API; GraphQL mutations with `agentAssignment`; user-to-server token requirement; PAT scopes; preview status |
| GitHub: Starting GitHub Copilot sessions | https://docs.github.com/en/copilot/how-tos/use-copilot-agents/cloud-agent/start-copilot-sessions | Primary | Full list of entry points (UI, mobile, IDE, REST API, CLI, MCP, Jira, Slack, Teams, Azure Boards, Linear, Raycast) |
| GitHub REST API: Issues | https://docs.github.com/en/rest/issues/issues | Primary | `POST /repos/{owner}/{repo}/issues` accepts `assignees` array |
| `sre-agent-workshop` — `scenarios/cloud-agent-handover/README.md` | https://github.com/JoranBergfeld/sre-agent-workshop/blob/main/scenarios/cloud-agent-handover/README.md | Primary | Human-in-the-loop pattern; fallback path; deliberate unassigned creation |
| `sre-agent-workshop` — `scenario.yaml` | https://github.com/JoranBergfeld/sre-agent-workshop/blob/main/scenarios/cloud-agent-handover/scenario.yaml | Primary | Incident type, alert module, difficulty, learning objectives |
| `sre-agent-workshop` — `sample-issue.md` | https://github.com/JoranBergfeld/sre-agent-workshop/blob/main/scenarios/cloud-agent-handover/sample-issue.md | Primary | Exact issue body the cloud agent receives; acceptance criteria |
| `sre-agent-workshop` — `knowledge/operational-guidelines.md` | https://github.com/JoranBergfeld/sre-agent-workshop/blob/main/scenarios/cloud-agent-handover/knowledge/operational-guidelines.md | Primary | Governs SRE Agent behavior: unassigned issue creation, human assigns `copilot-swe-agent`, no direct remediation |
| `sre-agent-workshop` — `docs/03-onboard-sre-agent.md` | https://github.com/JoranBergfeld/sre-agent-workshop/blob/main/scenarios/cloud-agent-handover/docs/03-onboard-sre-agent.md | Primary | Setup steps; knowledge base upload; Code Access connector |
| `sre-agent-workshop` — `docs/04-configure-incident-response.md` | https://github.com/JoranBergfeld/sre-agent-workshop/blob/main/scenarios/cloud-agent-handover/docs/04-configure-incident-response.md | Primary | Response plan config; `copilot-swe-agent` verification query; Review autonomy; GitHub Connector for issue access |
