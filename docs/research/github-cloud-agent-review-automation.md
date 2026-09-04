# Research: GitHub Cloud Agent and Copilot Review Automation

## Context

This research feeds DSF's `/to-spec` input for the Creation/Operation phase build↔operate loop revision (wayfinder ticket #154, part of map #151). It explicitly excludes Feature Council integration concerns and covers only GitHub-native mechanisms. This document is research only — no implementation decision is made here.

---

## Summary / Answer to the hypothesis

**Hypothesis under test:** Does GitHub natively support (a) auto-merge gated on a Copilot review outcome, and (b) a workflow that automatically triggers on a failed review and re-invokes code generation?

**Confirmed:**

- **Auto-merge gated on Copilot *approval*** is natively possible (public preview as of the research date). If "Copilot approvals" is enabled at the enterprise/org/repo level, Copilot can submit an approving review that satisfies the branch protection required-reviews rule, which then lets auto-merge proceed. This requires opting into Copilot approvals and configuring `enableAutoMerge` on the PR.
- **GitHub Actions can natively trigger on a "changes requested" review** via the `pull_request_review` event (`submitted` activity type, filtered by `github.event.review.state == 'changes_requested'`). This is fully supported workflow YAML.
- **A failed required CI check** (Checks API `conclusion: failure` or a failed Commit Status) natively blocks auto-merge and can be observed via the `check_run` / `check_suite` / `workflow_run` Actions events.

**Not confirmed / requires custom glue:**

- **Copilot code review does NOT submit "Request Changes"** reviews by default — only "Comment" or (optionally) "Approve." There is no native "Copilot changes requested" review state that blocks auto-merge, so gating auto-merge on "Copilot says no" requires a separate custom check.
- **Copilot Cloud Agent does NOT automatically react to failed CI checks or "changes requested" reviews on its own PRs** once its session is complete. There is no documented self-repair loop triggered by post-PR feedback. The session ends; re-invocation requires a new explicit task start.
- **Calling the Cloud Agent REST API from a GitHub Actions workflow** is possible but requires a user-to-server token (PAT or GitHub App user token) — server-to-server installation tokens are explicitly excluded. This means fully automated "failed CI → re-invoke agent" loops need a stored user credential (PAT/GitHub App), which is custom glue, not a native platform behavior.

---

## 1. GitHub Cloud Agent (GitHub Copilot coding agent)

### What it is

GitHub Copilot cloud agent (formerly "coding agent") is an AI system that can autonomously research a repository, create an implementation plan, make code changes on a branch, and open a pull request. It runs in an ephemeral development environment powered by GitHub Actions. ([About GitHub Copilot cloud agent](https://docs.github.com/en/copilot/concepts/agents/cloud-agent/about-cloud-agent))

### Invocation / assignment

The agent bot is identified as `copilot-swe-agent[bot]`. It can be started from multiple clients:

- **GitHub Issues**: assign the issue to `copilot-swe-agent[bot]` via the Assignees UI or REST/GraphQL API
- **GitHub Agents tab / panel / Copilot Chat** (`/task` command): prompts Copilot to create a PR
- **Failing GitHub Actions runs**: start a session directly from a failed run in the Actions UI ([Starting GitHub Copilot sessions](https://docs.github.com/en/copilot/how-tos/use-copilot-agents/cloud-agent/start-copilot-sessions))
- **REST API** (`POST /agents/repos/{owner}/{repo}/tasks`) with a `prompt` field ([Using Copilot cloud agent via the API](https://docs.github.com/en/copilot/how-tos/use-copilot-agents/cloud-agent/use-cloud-agent-via-the-api))
- **GraphQL API** via `createIssue`, `updateIssue`, `addAssigneesToAssignable`, or `replaceActorsForAssignable` mutations (with `agentAssignment` input and the `GraphQL-Features` header) ([Using Copilot cloud agent via the API](https://docs.github.com/en/copilot/how-tos/use-copilot-agents/cloud-agent/use-cloud-agent-via-the-api))
- IDEs (VS Code, JetBrains, Eclipse, Visual Studio), GitHub CLI, GitHub MCP Server, Jira, Slack, Teams, Azure Boards, Linear, Raycast

### Session and event model

A cloud agent task has a lifecycle with states: `queued`, `in_progress`, `completed`, `failed`, `idle`, `waiting_for_user`, `timed_out`, `cancelled`. ([Using Copilot cloud agent via the API](https://docs.github.com/en/copilot/how-tos/use-copilot-agents/cloud-agent/use-cloud-agent-via-the-api))

The maximum execution time per session is **59 minutes** (hard limit, configurable shorter via `timeout-minutes` in `copilot-setup-steps.yml`). ([About GitHub Copilot cloud agent](https://docs.github.com/en/copilot/concepts/agents/cloud-agent/about-cloud-agent))

Each session:
- Is attributed to the user who started it (or created the automation)
- Logs are visible to all users with read access to the repository
- Copilot works on one branch and can open exactly one pull request per task
- Pull requests opened by the cloud agent are **not automatically triggered for GitHub Actions workflows** until a user with write access approves the workflow run (security safeguard) ([About Copilot automations](https://docs.github.com/en/copilot/concepts/agents/cloud-agent/about-automations#security-and-safety))
- The user who created the session/automation cannot approve their own PR (standard PR policy applies) ([About Copilot automations](https://docs.github.com/en/copilot/concepts/agents/cloud-agent/about-automations#security-and-safety))

### Behavior when CI check fails on its PR

**Not documented** that the cloud agent automatically retries or pushes new commits in response to a failed CI check on its own PR once the session completes. The session ends, and the PR sits with failing checks. The UI entry point "start a session from failing GitHub Actions runs" is available for a human to trigger a *new* agent session to investigate the failure. ([Starting GitHub Copilot sessions](https://docs.github.com/en/copilot/how-tos/use-copilot-agents/cloud-agent/start-copilot-sessions))

### Behavior when a review requests changes on its PR

**Not documented** as automatic behavior. Once the session completes, the agent does not observe post-PR review events on its own. A user can `@copilot` in a PR comment to ask it to make changes, which starts a new interactive session. For automated loops, a separate automation or Actions workflow would need to explicitly re-invoke the agent.

### Automations (scheduled / event-triggered)

The "Automations" feature (available in private/internal repos, all paid Copilot plans) lets you define a named automation with:
- **Triggers**: on a schedule (hourly/daily/weekly), when an issue is created, when a pull request is opened, **when a pull request is synchronized** (new commits pushed)
- **Prompt**: natural language task
- **Tools**: explicitly selected set of allowed actions (push changes, label issue, create PR, etc.)

Available triggers do **not** include "when a review is submitted" or "when a check fails." Those would require custom GitHub Actions glue calling the agent API. ([About Copilot automations](https://docs.github.com/en/copilot/concepts/agents/cloud-agent/about-automations), [Creating automations with Copilot cloud agent](https://docs.github.com/en/copilot/how-tos/use-copilot-agents/cloud-agent/create-automations))

### GitHub API surface

- **REST**: `POST /agents/repos/{owner}/{repo}/tasks` — start task; `GET /agents/repos/{owner}/{repo}/tasks` — list; `GET /agents/repos/{owner}/{repo}/tasks/{task-id}` — check status. **Only user-to-server tokens are supported** (PATs, OAuth app tokens, GitHub App user tokens). Server-to-server (installation) tokens are explicitly not supported. ([Using Copilot cloud agent via the API](https://docs.github.com/en/copilot/how-tos/use-copilot-agents/cloud-agent/use-cloud-agent-via-the-api))
- **GraphQL**: assign via `createIssue` / `updateIssue` / `addAssigneesToAssignable` / `replaceActorsForAssignable` with `agentAssignment` input, requiring `GraphQL-Features: issues_copilot_assignment_api_support,coding_agent_model_selection` header
- **REST issues API**: `POST /repos/{owner}/{repo}/issues/assignees` with `"assignees": ["copilot-swe-agent[bot]"]` and `agent_assignment` body

### Key limitations for DSF

- Cannot make changes across multiple repositories per session
- Cannot comply with branch protection rules requiring commit signing (access is blocked) ([About GitHub Copilot cloud agent](https://docs.github.com/en/copilot/concepts/agents/cloud-agent/about-cloud-agent#limitations-in-copilot-cloud-agents-compatibility-with-other-features))
- Automations are **not available in public repositories** ([About Copilot automations](https://docs.github.com/en/copilot/concepts/agents/cloud-agent/about-automations))
- The agent tasks API requires user-to-server tokens — cannot be called from a standard Actions workflow using `GITHUB_TOKEN` alone

---

## 2. Copilot code review

### How it's triggered

**Manual**: In the PR Reviewers sidebar, click "Request" next to Copilot, or via REST API by requesting `copilot-pull-request-reviewer[bot]` as a reviewer. ([Using GitHub Copilot code review](https://docs.github.com/en/copilot/how-tos/use-copilot-agents/request-a-code-review/use-code-review))

**Automatic** (repo-level configuration via Rulesets → "Automatically request Copilot code review"):
- When a PR is opened (as Open, not Draft)
- When a draft PR is switched to Open (first time)
- Optionally: **"Review new pushes"** — reviews every new commit pushed to the PR
- Optionally: **"Review draft pull requests"** — reviews while still a draft

([About GitHub Copilot code review](https://docs.github.com/en/copilot/concepts/agents/code-review), [Configuring code review by GitHub Copilot](https://docs.github.com/en/copilot/how-tos/copilot-on-github/set-up-copilot/configure-code-review))

Unless "Review new pushes" is enabled, Copilot only reviews a PR once. Manual re-review requires clicking the re-request sync icon.

### Output produced

- Review comments with severity labels (High / Medium / Low)
- Inline suggested changes (can be applied with one click or via "Fix with Copilot")
- An "overview comment" that includes an approval assessment
- **Review state**: by default, Copilot leaves a **"Comment"** review — this does NOT count toward required approvals and does NOT block merging ([Using GitHub Copilot code review](https://docs.github.com/en/copilot/how-tos/use-copilot-agents/request-a-code-review/use-code-review))

### "Fix with Copilot"

On review comments from Copilot, clicking **"Fix with Copilot"** invokes the cloud agent to address the specific feedback. It creates a draft comment where you can instruct Copilot, then you choose: create a new PR against the branch, or push a commit to the same PR. This is a **human-initiated** step, not automatic. ([Using GitHub Copilot code review](https://docs.github.com/en/copilot/how-tos/use-copilot-agents/request-a-code-review/use-code-review))

### Integration with required reviewers / branch protection / auto-merge

**Copilot approvals (public preview):** When enabled at the enterprise/org/repo level:
- Copilot can submit an **approving review** that satisfies required-approval rules (same as a human approval)
- Configuration: repo Settings → Copilot → Code review → "Allow Copilot to approve pull requests" + "Allow Copilot approvals to count toward merge requirements"
- Optional file path globs can restrict which PRs count
- If new commits are pushed after Copilot approves, the approval is dismissed (standard GitHub behavior for stale reviews)

**When approvals are OFF (default):** Copilot's review is only a "Comment" — it does not count toward merge requirements, does not block auto-merge, and does not satisfy required-review counts. ([About GitHub Copilot code review](https://docs.github.com/en/copilot/concepts/agents/code-review#copilot-approvals), [Configuring code review by GitHub Copilot](https://docs.github.com/en/copilot/how-tos/copilot-on-github/set-up-copilot/configure-code-review))

**Copilot does NOT submit "Request Changes" reviews.** There is no native mechanism for Copilot code review to block a PR from merging by submitting a "Request Changes" state. The closest is withholding approval (not approving), which matters only if Copilot approvals are required.

### Agentic capabilities

Copilot code review uses GitHub Actions runners for agentic features (full project context gathering). If GitHub Actions is unavailable or fails, reviews still generate but without those features. ([About GitHub Copilot code review](https://docs.github.com/en/copilot/concepts/agents/code-review#agentic-capabilities-for-copilot-code-review))

---

## 3. Auto-merge

### What it is

Auto-merge is a native GitHub feature that automatically merges a PR once all required reviews and status checks pass. ([Automatically merging a pull request](https://docs.github.com/en/pull-requests/how-tos/merge-and-close-pull-requests/automatically-merging-a-pull-request))

### Enabling it

- Must first be enabled for the repository in repo Settings
- On a PR with pending requirements: the "Enable auto-merge" button appears in the merge box
- Via GraphQL: `enablePullRequestAutomerge` mutation
- Via REST: `PUT /repos/{owner}/{repo}/pulls/{pull_number}/merge` (not exactly — auto-merge is enabled differently; the REST docs reference branch protection as prerequisite)
- Auto-merge is only shown/available when a PR has at least one pending required check or review condition

### Relationship to required status checks and required reviews

Auto-merge waits for:
1. All **required status checks** to pass (success/skipped/neutral)
2. All **required reviews** (N approvals from eligible reviewers)
3. Any other configured branch protection rules (conversation resolution, etc.)

A failed required status check **blocks auto-merge**. A "changes requested" from any human reviewer also blocks auto-merge (until dismissed or overridden). ([About protected branches](https://docs.github.com/en/repositories/configuring-branches-and-merges-in-your-repository/managing-protected-branches/about-protected-branches))

### Auto-merge is disabled if

- Someone without write permissions pushes new changes to the head branch
- The base branch is switched

### Copilot review interaction with auto-merge

- **Default (Copilot review = "Comment")**: No effect on auto-merge — Copilot's review is purely informational.
- **Copilot approval enabled and counting**: If configured, Copilot's approving review satisfies the required-approval count, allowing auto-merge to proceed once all checks also pass. If Copilot does not approve (issues an assessment without approval), auto-merge waits for human approval.
- **No "Copilot blocks merge" native state**: There is no native configuration where "Copilot found issues → blocks merge." Only the absence of approval (when Copilot approval is required) indirectly blocks it.

---

## 4. Checks and status checks

### Checks API vs. Commit Status API

**Commit Status API** (older):
- Set via `POST /repos/{owner}/{repo}/statuses/{sha}` by any authenticated user with push access
- States: `pending`, `success`, `failure`, `error`
- Simpler; one status per context name per commit ([REST API endpoints for commit statuses](https://docs.github.com/en/rest/commits/statuses))

**Checks API** (newer):
- Create/update check runs via `POST /repos/{owner}/{repo}/check-runs` — **only GitHub Apps can create check runs** (not OAuth apps or PATs)
- Check run `conclusion` values: `success`, `failure`, `neutral`, `cancelled`, `skipped`, `timed_out`, `action_required`
- Richer output: annotations, images, suggested actions
- Check suites group check runs for a commit ([REST API endpoints for check runs](https://docs.github.com/en/rest/checks/runs))

### Branch protection "required status checks"

- Configured in branch protection rules or rulesets
- A required status check must reach `success`, `skipped`, or `neutral` to allow merge
- Failed, pending, or timed-out checks block merge (and auto-merge)
- Can be set to "strict" (head branch must be up-to-date with base) or "loose" ([About protected branches](https://docs.github.com/en/repositories/configuring-branches-and-merges-in-your-repository/managing-protected-branches/about-protected-branches#require-status-checks-before-merging))
- You can specify which GitHub App must report a given check (to prevent spoofing)

### How a failed check surfaces to trigger automation

The `check_run` webhook event fires with activity type `completed` when a check run finishes. The `conclusion` field in the payload indicates failure. Similarly, `check_suite` fires `completed`. These events can trigger GitHub Actions workflows:

```yaml
on:
  check_run:
    types: [completed]
```

Then filter in the workflow:
```yaml
if: github.event.check_run.conclusion == 'failure'
```

**Important caveat**: The `check_run` and `check_suite` events do **not trigger workflows** if the check suite was created by GitHub Actions (to prevent recursive loops). ([Events that trigger workflows](https://docs.github.com/en/actions/reference/workflows-and-actions/events-that-trigger-workflows#check_run))

---

## 5. Workflows triggered by failed review / failed check

### Supported native trigger patterns

**Trigger on "changes requested" review:**

```yaml
on:
  pull_request_review:
    types: [submitted]

jobs:
  handle-changes-requested:
    if: github.event.review.state == 'changes_requested'
    runs-on: ubuntu-latest
    steps:
      - ...
```

This is fully supported. The `pull_request_review` event fires on `submitted`, `edited`, and `dismissed` activity types. The `github.event.review.state` property has the value `changes_requested`, `approved`, or `commented`. ([Events that trigger workflows](https://docs.github.com/en/actions/reference/workflows-and-actions/events-that-trigger-workflows#pull_request_review))

**Trigger on failed check:**

```yaml
on:
  check_run:
    types: [completed]

jobs:
  handle-failure:
    if: github.event.check_run.conclusion == 'failure'
    runs-on: ubuntu-latest
    steps:
      - ...
```

Also supported, but **not triggered for check runs created by GitHub Actions** (recursion prevention). ([Events that trigger workflows](https://docs.github.com/en/actions/reference/workflows-and-actions/events-that-trigger-workflows#check_run))

**Trigger on failed workflow run:**

```yaml
on:
  workflow_run:
    workflows: ["CI"]
    types: [completed]

jobs:
  handle-failure:
    if: github.event.workflow_run.conclusion == 'failure'
    ...
```

The `workflow_run` event fires when another named workflow completes and can access artifacts from that run. ([Events that trigger workflows](https://docs.github.com/en/actions/reference/workflows-and-actions/events-that-trigger-workflows))

### Native way to re-invoke GitHub Cloud Agent from a workflow?

**There is no fully automatic "native" way** — it requires custom glue:

1. The workflow triggered by `pull_request_review:submitted/changes_requested` or `check_run:completed/failure` runs custom steps.
2. In those steps, the workflow would call `POST /agents/repos/{owner}/{repo}/tasks` with a prompt describing the remediation needed.
3. **The blocking constraint**: The agent tasks API **only supports user-to-server tokens** (PATs, OAuth app user tokens, GitHub App user tokens). The `GITHUB_TOKEN` available by default in Actions workflows is a server-to-server installation token, which is explicitly not supported. ([Using Copilot cloud agent via the API](https://docs.github.com/en/copilot/how-tos/use-copilot-agents/cloud-agent/use-cloud-agent-via-the-api))
4. Therefore, a PAT or GitHub App user token must be stored as a repository/org secret and passed to the workflow step.

This is custom glue — provisioned by the repo owner (by setting up the workflow YAML + stored secret), but not a GitHub-native "one-click" capability.

### GitHub Agentic Workflows (public preview alternative)

A separate feature called **GitHub Agentic Workflows** allows defining AI-powered repository automations in markdown+frontmatter files committed to the repo, compiled to `.lock.yml` files and run as GitHub Actions workflows. These support standard Actions triggers (including `pull_request_review`, `check_run`, etc.) and can invoke AI coding agents (Copilot, Claude, Codex, Gemini). ([About GitHub Agentic Workflows](https://docs.github.com/en/copilot/concepts/agents/about-github-agentic-workflows))

This is in public preview and may offer a more structured path to AI-mediated retry loops with security guardrails (firewalled containers, read-only by default, declared safe-outputs). However it is **not the same as** Copilot cloud agent automations — it is a distinct feature using GitHub Actions as the runner.

### What about Copilot review triggering cloud agent automatically?

The "Fix with Copilot" button on a Copilot review comment is **human-initiated** — not automatic. There is no documented event or webhook that fires specifically when "Copilot found issues in a review" distinct from the generic `pull_request_review:submitted` event. A workflow would need to filter on `github.event.review.user.login == 'copilot-pull-request-reviewer[bot]'` to detect a Copilot-specific review. This filtering is custom logic.

---

## 6. What DSF can provision itself vs. opaque platform behavior

### ✅ Provisionable by DSF (via branch protection config, workflow YAML, API calls, or repo settings)

- **Branch protection rules** (required status checks, required review count): configured via repo/org Settings or API (`PUT /repos/{owner}/{repo}/branches/{branch}/protection`)
- **Rulesets** with "Automatically request Copilot code review" on open / on push / on draft: configured in repo Settings → Code and automation → Rulesets
- **Copilot approvals enabled and counting toward merge requirements**: configured in repo Settings → Copilot → Code review → two toggles (public preview)
- **Auto-merge** on individual PRs: enabled via REST `enablePullRequestAutomerge` GraphQL mutation or "Enable auto-merge" button; repo setting to allow auto-merge must first be enabled
- **GitHub Actions workflows triggered by `pull_request_review:submitted`** (filtering on `review.state == 'changes_requested'` and optionally `review.user.login == 'copilot-pull-request-reviewer[bot]'`)
- **GitHub Actions workflows triggered by `check_run:completed`** (filtering on `conclusion == 'failure'`)
- **GitHub Actions workflows triggered by `workflow_run:completed`** (filtering on `conclusion == 'failure'`)
- **Calling the cloud agent REST API from a workflow step** using a stored PAT/GitHub App user token to start a new agent task in response to a review or check failure
- **Copilot cloud agent automations** triggered by "when a PR is opened" or "when a PR is synchronized" (via the repo's Agents → Automations UI)
- **Custom instructions** (`.github/copilot-instructions.md`, `AGENTS.md`, `REVIEW.md`, path-specific `.github/instructions/**/*.instructions.md`) that shape both cloud agent behavior and code review behavior
- **Agent skills** (`.github/skills/`) that extend Copilot code review's capabilities
- **MCP server configuration** for the repository (GitHub and Playwright MCP servers are on by default)
- **Environment customization** (`.github/workflows/copilot-setup-steps.yml` or `.github/workflows/copilot-code-review.yml`): pre-install tools, set OS, configure firewall rules for the agent environment
- **GitHub Agentic Workflows** (public preview): commit markdown workflow files + compiled lock files to trigger AI-assisted automation on any standard Actions trigger, with declared safe-outputs and security guardrails

### ❌ Opaque / not controllable by repo owner

- **Copilot cloud agent's internal session lifecycle**: once started, the agent decides what to explore, how to structure commits, and when to stop — repo owner cannot inject mid-session instructions except via chat in the agents panel
- **Copilot cloud agent does not automatically self-repair** based on post-PR CI failures or "changes requested" reviews — there is no documented internal retry loop once the session ends; the session is over
- **Copilot code review does not emit "Request Changes"**: the review state is always "Comment" (or "Approve" if approvals are enabled) — there is no way to configure Copilot to submit a blocking "Request Changes" review
- **Cloud agent PR workflow approval**: GitHub Actions workflows on cloud agent PRs require a human with write access to approve the first run — this is a platform security behavior, not configurable off for public repos
- **Cloud agent attribution and self-merge restriction**: the creator of the automation cannot approve their own agent's PR; this is enforced by the platform
- **Which model Copilot code review uses**: model is an internal platform choice; repos cannot switch models for code review (only Copilot Chat model selection is user-controlled)
- **Automations in public repos**: not available; platform restriction not overridable

---

## Open questions / gaps

1. **Webhook for Copilot review specifically**: No primary source confirms whether `copilot-pull-request-reviewer[bot]` emits a distinct webhook event type vs. generic `pull_request_review`. DSF would need to filter by reviewer login. Testing recommended.

2. **Copilot approvals + auto-merge interaction end-to-end**: The "Copilot approvals" feature is in public preview. The exact behavior when Copilot's approval is the *only* required approval and CI is also passing (auto-merge fires) hasn't been verified by primary source with a concrete end-to-end example. Docs describe the pieces separately.

3. **`check_run` recursion prevention**: The docs say `check_run` events don't trigger workflows if the check suite was created by GitHub Actions. DSF needs to verify whether Copilot code review's check run (which uses Actions runners internally) counts as "created by GitHub Actions" and would therefore be ineligible as a `check_run` trigger. If so, the `pull_request_review` event path (on Copilot's review comment) is more reliable.

4. **Agent task API with `GITHUB_TOKEN`**: The API explicitly excludes server-to-server tokens. Whether a GitHub App installed in the org and using its user token (not installation token) can be used in a workflow to call this API is architecturally possible but not explicitly documented with a step-by-step example. This is a gap that needs a proof-of-concept.

5. **GitHub Agentic Workflows stability**: This feature is in public preview and the reference docs are at `github.github.com/gh-aw/` (a separate site). The feature's trigger model, security model, and GA timeline are not clear from primary docs as of this research.

6. **Auto-merge and automations visibility restriction**: Automations are not available for public repositories. If DSF's target repos are public, the native Copilot automations path is entirely unavailable, and all automation would need to be done through GitHub Actions workflows.

---

## Sources

- [About GitHub Copilot cloud agent](https://docs.github.com/en/copilot/concepts/agents/cloud-agent/about-cloud-agent)
- [Starting GitHub Copilot sessions](https://docs.github.com/en/copilot/how-tos/use-copilot-agents/cloud-agent/start-copilot-sessions)
- [Using Copilot cloud agent via the API](https://docs.github.com/en/copilot/how-tos/use-copilot-agents/cloud-agent/use-cloud-agent-via-the-api)
- [Creating automations with Copilot cloud agent](https://docs.github.com/en/copilot/how-tos/use-copilot-agents/cloud-agent/create-automations)
- [About Copilot automations](https://docs.github.com/en/copilot/concepts/agents/cloud-agent/about-automations)
- [Using Copilot cloud agent on GitHub](https://docs.github.com/en/copilot/how-tos/use-copilot-agents/cloud-agent/use-cloud-agent-on-github)
- [Using GitHub Copilot code review](https://docs.github.com/en/copilot/how-tos/use-copilot-agents/request-a-code-review/use-code-review)
- [About GitHub Copilot code review](https://docs.github.com/en/copilot/concepts/agents/code-review)
- [Configuring code review by GitHub Copilot](https://docs.github.com/en/copilot/how-tos/copilot-on-github/set-up-copilot/configure-code-review)
- [Automatically merging a pull request](https://docs.github.com/en/pull-requests/how-tos/merge-and-close-pull-requests/automatically-merging-a-pull-request)
- [About protected branches](https://docs.github.com/en/repositories/configuring-branches-and-merges-in-your-repository/managing-protected-branches/about-protected-branches)
- [REST API endpoints for check runs](https://docs.github.com/en/rest/checks/runs)
- [Events that trigger workflows — check_run](https://docs.github.com/en/actions/reference/workflows-and-actions/events-that-trigger-workflows#check_run)
- [Events that trigger workflows — check_suite](https://docs.github.com/en/actions/reference/workflows-and-actions/events-that-trigger-workflows#check_suite)
- [Events that trigger workflows — pull_request_review](https://docs.github.com/en/actions/reference/workflows-and-actions/events-that-trigger-workflows#pull_request_review)
- [Events that trigger workflows — pull_request](https://docs.github.com/en/actions/reference/workflows-and-actions/events-that-trigger-workflows#pull_request)
- [About GitHub Agentic Workflows](https://docs.github.com/en/copilot/concepts/agents/about-github-agentic-workflows)
