# GitHub authorization for `dsf onboard decide`

Research date: **2026-09-14**. Question: [Establish GitHub CLI authentication and App-approval capabilities](https://github.com/JoranBergfeld/dark-software-factory/issues/197), under [Onboard existing applications with dsf onboard decide](https://github.com/JoranBergfeld/dark-software-factory/issues/189).

**Status:** research findings, not an approved design or implementation specification.
Scope: attach a Feature Council to an existing repository; preserve ownership, visibility, actual default branch, protections, and delivery workflows. No Creation/Operation takeover. Azure authorization is a separate investigation.

## Findings that determine the feasible routes

1. **Using `gh` is not a credential type.** Its normal login uses the GitHub CLI OAuth app, but environment tokens override stored credentials and may represent another user or an installation. OAuth scopes do not elevate the user's underlying authority. [S1] [S2] [S3] [S4]
2. **Repository visibility is not App-installation authority.** Repository reads, issue writes, installing an App, extending its repository selection, and approving increased App permissions are different capabilities. The ordinary add-repository endpoint explicitly accepts **only a classic PAT with `repo`**, from a repository administrator. A GitHub CLI OAuth token is not a supported substitute, even with `repo`. [S5] [S6] [S7]
3. **GitHub CLI is a privileged OAuth app.** GitHub explicitly exempts it from ordinary organization OAuth-app access restrictions. Do not tell an operator that the standard GitHub CLI OAuth app needs the same owner approval as an arbitrary OAuth app. This exemption does not install the DSF App or remove SSO, repository-role, endpoint-token-type, or network requirements. [S8] [S9] [S10] [S11]
4. **The ordinary handoff is GitHub's installation/configuration UI.** A personal-account owner or an eligible organization actor grants access there; organization policy can require owner approval or even disable installation requests. An already authorized, correctly scoped installation needs no repository-selection mutation. [S6] [S12] [S13]
5. **There is a current enterprise exception to "Apps cannot be installed by API."** GitHub Enterprise Cloud has installation-management APIs for an App already installed on the owning enterprise with explicit enterprise permissions. These do not accept ordinary `gh` OAuth or PAT credentials. They are a distinct administrator-delegated route, not a council-runtime permission. [S14] [S15]
6. **Durable filing identity is the DSF App installation, not the onboarding human.** Repository metadata reads plus `Issues: write` cover repository identification, issue reads, and issue filing; code/charter evidence adds `Contents: read`; PR evidence adds `Pull requests: read`. Other evidence sources have separate permissions. Installation tokens expire after one hour and can be narrowed to already granted repositories and permissions. [S16] [S17] [S18] [S19] [S20] [S40]

## 1. Evidence and terminology

Primary evidence: official GitHub REST references, App-authentication and administration docs, GitHub CLI manuals, and first-party source. REST token-type sections were read from the HTML pages as well as their prose; the simplified Markdown representation omitted some of those sections. An OpenAPI `enabledForGitHubApps` boolean alone is **not** a sufficient token-type matrix.

Source snapshots:

- `github/rest-api-description` at `e16cc2584c6d32e8cd4d461759e1475e79327d6c` (2026-09-12), both ordinary GitHub.com and Enterprise Cloud descriptions. [S37] [S38]
- `cli/cli` at `38316c1c4f275030e3df6666382922e75410d68b` (2026-09-14); OAuth flow and scope handling. [S3] [S47]
- `github/docs` at `00f535f7c8a6428ef5377dc0672e7840886df37b` (2026-09-14); pinned enterprise-installation tutorial. [S15]
- DSF local observations at base `b427b762e7f5a9982b64059c995802d8b8d5920c`; these are implementation facts, not decisions for onboarding.

The current REST documentation describes API version `2026-03-10`. GitHub also documents `2022-11-28` as supported until March 10, 2028; unversioned requests currently default to that older version. Authorization requirements can change between API versions. An implementation must name its version rather than silently inherit the documentation site's current default. The add-repository restriction was also present on the reference requested with `apiVersion=2022-11-28`. [S5] [S36]

Terms below distinguish **operator** (the authenticated human), **App registration** (identity, requested permissions, distribution), **installation** (one account's consent and repository access), **App user access token** (App acting for a human), **installation access token** (App acting as its installation), and **App JWT** (App-authenticated control-plane requests). Installing and authorizing an App are independent: either can happen without the other. [S6] [S16] [S17]

No target application repository was mutated or used for live authorization experiments; no tokens were inspected or extracted, no live App changes were made, and no human credential persistence is proposed. Examples and preflight results below describe feasible future behavior, not actions performed during research.

## 2. Credential and endpoint capability matrix

### Credential meanings

| Credential presented through `gh` or an API client | Authority and constraints |
| --- | --- |
| GitHub CLI's normal OAuth token | CLI source requests `repo`, `read:org`, and `gist`, plus requested additional scopes. This is an OAuth-app token, **not a PAT** and not a DSF App user token. Existing credentials may differ from login defaults. [S1] [S3] [S4] |
| Classic PAT | Acts as its human owner, limited by OAuth-style scopes and organization/SSO policy; cannot elevate that owner's role. `repo` is broad, not a one-repository grant. [S4] [S9] [S21] |
| Fine-grained PAT | Acts as its human owner; restricted to one resource owner, selected repositories and endpoint permissions. Organization approval may be pending, in which case it can only read public resources. Current documented gaps include outside/repository-collaborator use and contributing to public repositories outside the token's supported owner scope. [S21] [S22] |
| GitHub App user access token | Requires that user to authorize the particular App; effective access is the intersection of user access and App access/permissions. For installed-account resources, the App must be installed there. Authorization of GitHub CLI does not authorize DSF. [S17] |
| GitHub App installation access token | Acts as the App installation, independently of the onboarding human's permissions. Restricted to installation grants and any narrower token request; expires after one hour. An ordinary repository/organization installation is not an enterprise installer. [S16] [S23] |
| App JWT | Authenticates the App itself for installation discovery and minting installation tokens. Not a general repository-content or issue-filing credential. [S16] [S24] |

`GH_TOKEN` takes precedence over `GITHUB_TOKEN`, and both override stored credentials for GitHub.com and subdomains of `ghe.com`. Enterprise Server has the separate `GH_ENTERPRISE_TOKEN` / `GITHUB_ENTERPRISE_TOKEN` precedence. Environment variable names identify a credential **source**, not the principal or credential class. A workflow's `GITHUB_TOKEN` is not the operator's personal login. [S2] [S9]

### Ordinary repository/organization APIs

**Yes** means the endpoint documents support, subject to all listed role, scope, resource and policy constraints. **ND** means no documented support found for that credential on that endpoint; not a tested rejection and not an implementation dependency. **No** means an explicit exclusion or an endpoint reserved for another authentication mode. Public-resource read exceptions never establish installation membership.

| Operation | CLI OAuth | Classic PAT | Fine-grained PAT | App user token | Installation token |
| --- | --- | --- | --- | --- | --- |
| `GET /repos/{owner}/{repo}` | Yes; private access needs appropriate scope/user access | Yes; same | Yes; `Metadata: read` | Yes; `Metadata: read` | Yes; `Metadata: read` [S18] |
| Read code / read issues / create issue | Yes; appropriate public/private scopes and user access | Yes; same | Yes; endpoint permissions and selected owner/repository | Yes; intersection of App/user grants | Yes; installation grants; permissions below [S4] [S19] [S20] |
| `GET /user/installations` | ND | ND | No | Yes; installations of **that App** accessible to the user; no extra permission | No [S5] |
| `GET /user/installations/{id}/repositories` | ND | ND | No | Yes; `Metadata: read`, filtered to user-accessible repositories | No [S5] |
| `GET /installation/repositories` | ND | ND | No | No | Yes; repositories accessible to this installation token, no extra permission [S5] |
| `PUT /user/installations/{id}/repositories/{repository_id}` | **No** | **Yes: `repo` + repository admin**, subject to organization restrictions | **No** | **No** | **No** [S5] [S12] |
| `GET /orgs/{org}/installations` | Documented for organization owner; scope caveat below | Same caveat | Yes; organization `Administration: read`, human owner requirement | Yes; same permission, human owner requirement | Listed as supported with organization `Administration: read`; do not reinterpret as personal ownership [S25] |
| Discover a repository's installation with `GET /repos/{owner}/{repo}/installation`; inspect `GET /app/installations/{id}` | No | No | No | No | No: these require the **App JWT** [S24] |
| Mint via `POST /app/installations/{id}/access_tokens` | No | No | No | No | No: requires the **App JWT**, produces an installation token [S16] [S24] |
| Install a new ordinary personal/org installation or approve increased permissions | No general token-only route established here | Same | Same | User authorization alone is not installation consent | Cannot self-grant ordinary installation authority; enterprise exception below [S6] [S7] [S14] |

Important qualifications:

- The ordinary **add** endpoint says "only ... PATs (classic) with the `repo` scope." The **remove** endpoint has the same restriction, requires `repository_selection=selected`, and can reject removing the last repository. The removal endpoint does not authorize removal during onboarding/offboarding; ownership remains a separate decision. [S5]
- `/user/installations` is not documented as an inventory of every App installed for a human; it lists installations of the App represented by the App user token. A successfully visible repository does not prove these `/user/installations/...` reads work with the operator's OAuth/PAT credential. [S5] [S17]
- The organization-wide inventory reference literally specifies OAuth/classic scope **`admin:read`**, while GitHub's scope catalogue defines `admin:org` / `read:org`, not `admin:read`. This is a documentation inconsistency, not a basis for inventing a scope or promising ordinary CLI defaults suffice. That inventory is optional: App-JWT discovery and installation-scoped checks provide other documented routes. [S4] [S24] [S25]
- Never infer privileges from an empty `X-OAuth-Scopes` header. CLI source itself stops attempting capability inference in that case. `X-Accepted-GitHub-Permissions` describes endpoint requirements, not a complete inventory of the caller's grants. [S47] [S26]

### Current Enterprise Cloud exception

These endpoints are for a GitHub App **already installed on the enterprise that owns the organization**. The REST references list **App user access tokens and App installation access tokens**, not fine-grained PATs; the endpoint's App-only restriction rules out CLI OAuth and classic PATs. GitHub's tutorial demonstrates a separate enterprise-scoped installer App and an organization-scoped automation App. It does not demonstrate ordinary repository credentials being upgraded into enterprise authority. [S14] [S15]

| Enterprise route | Required enterprise permission; behavior |
| --- | --- |
| `GET /enterprises/{enterprise}/apps/organizations/{org}/installations` | `Enterprise organization installations: read`; lists all Apps installed on that organization, regardless of App owner. [S14] |
| `POST /enterprises/{enterprise}/apps/organizations/{org}/installations` | `Enterprise organization installations: write`; accepts target App `client_id`, `repository_selection` (`all`, `selected`, or `none` for an App without repository permissions), and repository names when selected. [S14] |
| `GET /enterprises/{enterprise}/apps/organizations/{org}/installations/{id}/repositories` | Either `Enterprise organization installation repositories: read` or `Enterprise organization installations: read`. [S14] |
| `PATCH /enterprises/{enterprise}/apps/organizations/{org}/installations/{id}/repositories/add` | Either corresponding enterprise permission at write level; adds up to 50 repository names, does not add an already present repository again. [S14] |
| `PATCH .../installations/{id}/repositories` | Same write alternatives; switches between all/selected repository access. This is wider than adding one repository. [S14] |

**Non-obvious side effects:** enterprise installation `POST` can unsuspend an existing installation, approve pending installation requests, and accept a pending permission update. It is therefore **not a harmless "ensure installed" probe**. Repository discovery and membership reads are separate GETs. The tutorial's enterprise-owner setup and installer-App installation still require prior human authority; an already established enterprise installer can subsequently operate without per-call browser consent. [S14] [S15]

This is a feasible enterprise-specific integration option for later consideration, not a recommendation to grant enterprise-wide install authority to the council or a bypass for an organization's governance.

## 3. Non-mutating repository and permission discovery

`gh api` can make authenticated requests using CLI credential resolution without DSF extracting the credential. Specify the hostname and selected owner/name explicitly; avoid implicit `{owner}`, `{repo}`, `{branch}` substitution from the working directory. Use explicit `--method GET` when adding query fields: `gh api` otherwise changes to POST when fields are supplied. Paginate inventories. These are supported CLI mechanics; wrapping them as the onboarding transport is a feasible option, not a settled implementation choice. [S2] [S27]

| Read-only observation | What it establishes; what it does not |
| --- | --- |
| Active host/account status, then `GET /user` with the same effective credential | Identifies the authenticated human when using a user credential; repository visibility alone is not identity validation. `gh auth status --active --hostname HOST` does not reveal the full token unless explicitly asked to show it. Its JSON mode exits zero even for authentication problems, so inspect the result instead of treating that exit code as success. [S28] [S9] |
| `GET /repos/{owner}/{repo}` | Capture numeric `id`, `node_id`, canonical `full_name`, owner `id`/`login`/`type`, `default_branch`, `visibility`, `private`, `archived`, `disabled`, `has_issues`, and returned `permissions`. The schema exposes these fields; permissions/settings can be absent or authority-dependent. A successful public read is not proof of private access or write authority. [S18] [S37] |
| `gh repo view OWNER/REPO --json ...` | Documented convenience fields include `id`, `nameWithOwner`, `defaultBranchRef`, `viewerPermission`, `viewerCanAdminister`, `visibility`, `isArchived`, `isEmpty`, `hasIssuesEnabled`. Do not substitute its GraphQL ID for the numeric repository ID required by the REST add-repository endpoint. [S5] [S29] |
| `GET /repos/{owner}/{repo}/collaborators/{login}/permission` | Reports `permission` and `role_name`, including custom-role names; `maintain` maps to legacy `write`, `triage` to `read`. Highest grant wins; the API cannot distinguish organization-level from repository-level grants. Fine-grained permission: `Metadata: read`. This does not describe organization App-install policy. [S30] |
| Read selected default-branch contents with explicit `ref` | `Contents: read` can inspect charter files and `.github/workflows` without writing them. Record the actual default branch rather than inventing `main`; distinguish an empty/unreadable repository from a usable evidence tree. [S20] [S29] |
| `GET /repos/{owner}/{repo}/rulesets?includes_parents=true` and ruleset detail GETs | `Metadata: read`; inventories repository and applicable higher-level rulesets. This is different from replacing or normalizing them. [S31] |
| `GET /repos/{owner}/{repo}/rules/branches/{branch}` | `Metadata: read`; returns active applicable rules, including higher-level rules, but not disabled/evaluate rulesets. The branch need not exist: this response alone does not prove a default branch exists. [S31] |
| `GET /repos/{owner}/{repo}/branches/{branch}/protection` | Classic branch-protection details require repository `Administration: read` for fine-grained credentials. Inability to inspect is not permission to change or remove protection. A 404 is not, by itself, proof of no protections. [S26] [S32] |
| `GET /repos/{owner}/{repo}/interaction-limits` | `Administration: read`; reports public-repository interaction restrictions, their origin and expiration, or an empty response for none. Restrictions can originate from the owning user/organization. Their effect on a particular App action is not fully proven by this read. [S33] |

**Implementation implications, not new policy:** preserve the selected repository's numeric identity alongside current names; surface redirects/owner changes for revalidation rather than silently selecting another repository. Re-read identity/default branch before any separately approved write. Treat absent fields and denied policy-inspection reads as **unknown**, not false. GitHub documents redirected repository reads and intentionally ambiguous 404 responses. [S18] [S26]

No all-purpose "can install this App now" capability is established by these repository APIs. Successful operator discovery does not establish a correctly selected DSF installation, approved App permission version, runtime connectivity, or the absence of external automation. [S5] [S6] [S7] [S11]

## 4. Installation, approval, and browser handoffs

| Situation | Documented boundary and feasible next action |
| --- | --- |
| Personal repository; operator owns the personal account | That user can install an App on the account. A collaborator's repository access does not authorize installation on somebody else's personal account; hand off to the account owner. [S6] |
| Organization owner | Can install Apps on the organization, subject to applicable enterprise/network/SSO constraints and App availability. [S6] [S10] [S11] |
| Organization repository admin | May install only if the App requests **neither organization permissions nor repository Administration**, and organization policy permits repository-admin installation; may select only repositories they administer. [S6] [S12] |
| Organization restricts installs to owners | Repository admins cannot install **or add their repositories to existing installations**. They must request owner action. If requests are also disabled, direct administrator handoff is required instead of promising a request button. [S12] |
| Organization member/outside collaborator without installation authority | Installation UI may offer `Request` or `Install and request`; a request is not completed authorization. App-manager role by itself does not confer organization installation authority. [S6] [S12] |
| Private DSF App owned by a different account | Private App registrations can only be installed on their owning account. Being able to access the target organization does not make a personally owned private App installable there. Same-owner registration or an appropriately distributable App are feasible alternatives for the human ticket; changing App visibility/ownership is not authorized here. [S34] |
| App already installed, repository already included | No access expansion needed. Verify actual App ID, installation account/ID, suspension, repository membership, and granted permissions; a remembered `all` setting is not evidence by itself. [S5] [S24] |
| App installed, repository missing | Browser **Configure** can change repository access. Alternatively, the ordinary classic-PAT/admin API or the separately delegated enterprise route may work under their distinct constraints. No supported CLI-OAuth equivalent was established. [S5] [S12] [S13] [S14] |
| App requests more permissions | Existing installations retain old permissions until the owner accepts the update. New registration permissions or a new App release do not establish that this installation approved them. [S7] [S23] |
| SSO-protected account | Start an active organization/enterprise SSO session before installing/requesting/authorizing the App; missing organizations can be an SSO symptom. App user-token resource access may require reauthorization after establishing that session. Classic PATs need separate SSO authorization; fine-grained PATs are authorized during creation. [S9] [S10] |

The documented new-install URL is `https://github.com/apps/APP-SLUG/installations/new`; existing installations are managed through **Configure** in account/organization settings. The browser session may be another user from the terminal. GitHub explicitly warns that a setup callback's `installation_id` can be spoofed; its documented user-association check uses an App user access token. [S6] [S13] [S35]

Two different verification goals must stay separate:

- **Resource grant:** App-JWT discovery plus installation-token repository membership and permission evidence can establish that the intended App currently covers the intended repository. Public-content reads alone cannot prove membership. [S5] [S16] [S24]
- **Which human approved:** the operator's CLI identity or a pasted installation ID does not prove who performed browser consent. GitHub documents the App-user-token association check; if the eventual design permits owner handoff without authorizing a DSF user token, it must not claim that this proves the same-human association. [S17] [S35]

For an ordinary first installation, SSO or owner approval may make a fully headless flow impossible. **Already installed and adequately granted** can be headless; **already delegated enterprise installer** is another qualified headless route. Neither entails reusing the human credential as runtime identity. [S6] [S10] [S14] [S16]

## 5. Council runtime minimum permissions

These are endpoint-specific minima, **not a request to enable every evidence source**. Rows describe permissions on the selected repository unless explicitly noted. Private-resource access requires actual installation coverage. The selected evidence/readiness contract determines which optional rows are needed. [S16] [S23]

| Work | Minimum App permission | Boundary |
| --- | --- | --- |
| Identify repository / inspect rulesets | `Metadata: read` | No repository Administration needed for these reads. [S18] [S31] |
| Read repository issues / deduplicate / file proposals | `Issues: write` for filing; includes needed issue-read capability | `GET/POST /repos/{owner}/{repo}/issues`; filing does not inherently require code write, PR write, Actions write, or Administration. [S19] |
| Add council labels / label issues, if approved | `Issues: write` is sufficient | Label endpoints also accept `Pull requests: write` as an alternative, not an additional requirement; no reason to add that permission solely for labels. [S39] |
| Read code, repository documents, charter, workflow definitions | `Contents: read` | No `Workflows: write` needed for REST file reads; exact paths/refs still need to exist and be accessible. [S20] |
| Read PR evidence | `Pull requests: read` | Dedicated PR endpoints; issue-list responses are not a substitute for full PR evidence. [S40] |
| Read Actions runs/logs, if selected | `Actions: read` | Does not require changing or running workflows. [S41] |
| Read deployment records, if selected | `Deployments: read` | Does not authorize deployments. [S42] |
| Read check runs / commit statuses, if selected | `Checks: read` / `Commit statuses: read` respectively | Separate API permission families, not implicit in metadata. [S43] [S44] |
| Inspect classic protection / interaction restrictions, if needed | `Administration: read` | Optional privileged inspection, not an issue-filing minimum; requesting Administration can itself force organization-owner installation. [S6] [S32] [S33] |
| Write council-owned repository files, if separately approved | `Contents: write`; opening a PR additionally needs `Pull requests: write` | Branch/ruleset requirements still apply. These grants are unnecessary if the runtime only reads files and files issues. [S20] [S31] [S40] |
| Modify workflow files | Additional `Workflows: write` for relevant App file writes; OAuth/classic uses `workflow` scope | Not part of the approved council-only research scope. [S20] |

GitHub says any user with repository pull access can create an issue, but creating one with a fine-grained/App token still requires the endpoint's `Issues: write` permission. For user-authenticated issue creation, labels, assignees and milestones can be silently dropped without push access. Do not demand human repository admin just to read/file; do not mistake issue creation for proof all requested metadata was applied. App label endpoints have their own explicit permission contract above. [S19] [S39]

Installation tokens can request one `repository_id` and a subset of already granted permissions. They cannot add an uninstalled repository, approve a permission increase, or enlarge an installation. Without an explicit repository/permission subset, token creation defaults to all available installation repositories/permissions. [S16]

**Isolation caveat (inference):** narrowing a short-lived token is not the same as narrowing the App registration, installation consent, or the authority of whoever holds its signing key. The documented JWT minting path can request tokens for the App's installations; a process holding that App's key is not confined merely because one particular token was narrowed. Likewise repository-level Issues/Contents permissions are not a server-enforced "DSF issues only" or "one charter path only" boundary. Keep these authority levels distinct in [Define council-only infrastructure and runtime permissions](https://github.com/JoranBergfeld/dark-software-factory/issues/191). [S16] [S23] [S24]

**Workflow-preservation caveat:** issues, labels and comments are observable events. GitHub documents that App installation tokens can trigger workflows, unlike the ordinary event-recursion suppression associated with a workflow's own `GITHUB_TOKEN`. Thus "we did not edit CI" does **not** prove "we did not start existing delivery automation." Inspecting accessible workflow files can identify risks; it cannot establish the absence of external integrations. Avoiding those triggers is a human repository-contract decision in [Define repository additions without delivery takeover](https://github.com/JoranBergfeld/dark-software-factory/issues/196), not an automatic permission fix. [S45]

## 6. Concrete preflight outcomes and proof limits

The labels below are suggested diagnostic vocabulary, not approved product status names. Each result should retain the host, repository ID/name, credential source/principal where known, endpoint, HTTP status, relevant non-secret diagnostic headers, and the next responsible actor. Do not log credentials, full auth-debug traces, or callback authorization codes.

| Observation / proposed result | Actionable interpretation |
| --- | --- |
| `OperatorAuthenticationRequired`: missing login, 401, active-account error | Ask the operator to authenticate to the selected host through their normal CLI flow; an environment override or different active account may be the cause. Never extract/store a human token to "fix" runtime auth. [S1] [S2] [S9] [S28] |
| `RepositoryUnreadableOrMissing`: 404 | Confirm host and canonical repository; check actor, token resource scope, SSO and policy. GitHub intentionally uses 404 for inaccessible private resources. **Do not create a replacement repository.** [S26] |
| `IdentityOrDefaultBranchChanged` | Revalidate the recorded ID/owner/name/default branch and selection before a separately approved write; do not silently transfer ownership or apply a `main` default. [S18] [S37] |
| `IssueFilingUnavailable`: archived, disabled, or `has_issues=false` | Archived issues are read-only; disabled issues yield 410 on issue creation. Do not unarchive, enable issues, or otherwise rewrite settings as a preflight repair. [S18] [S19] [S46] |
| `PermissionInspectionIncomplete` | A forbidden/ambiguous protection, interaction-policy or role read remains unknown; it does not prove safe writes or no protections. [S26] [S30] [S32] [S33] |
| `InstallationNotVerified` | Repository read succeeded but no matching App/installation/membership evidence. Use a supported App-side read or owner handoff; do not probe an unsupported credential by attempting a mutation. [S5] [S24] |
| `InstallationRepositoryGrantRequired` | Correct App exists but selected repository is absent. Present ordinary browser/classic-PAT-admin routes, or explicitly qualified enterprise route; never silently broaden to all repositories. [S5] [S13] [S14] |
| `CredentialCannotSelectInstallationRepositories` | Operator is CLI-OAuth/fine-grained-PAT/App-user/ordinary-installation authenticated and the intended operation is the ordinary add-repository PUT. Use a supported approval route, not more OAuth scopes or retry. [S5] |
| `OwnerApprovalRequired` / `InstallationRequestsDisabled` | Repository admin is not sufficient under organization policy or the App requests Administration/organization permissions. Identify owner action; do not promise a request can be submitted when requests are disabled. [S6] [S12] |
| `AppUnavailableForTargetOwner` | Private App owner differs from repository owner. Escalate the App ownership/distribution prerequisite; changing repository ownership/visibility is not a remedy. [S34] |
| `InstallationSuspended` / `PermissionUpdateRequired` | Inspect actual installation state/permissions; owner/App-owner intervention may be needed. Do not call enterprise install POST as a read: it may unsuspend or approve updates. [S7] [S13] [S14] [S24] |
| `SsoAuthorizationRequired` | For classic PAT 403, GitHub may return a one-hour `X-GitHub-SSO` authorization URL; cross-org reads can be partial. For App UI/user-token flows, establish active SSO and follow documented reauthorization/install steps. [S9] [S10] |
| `PatPolicyOrApprovalBlocked` | Organization can block PAT classes, enforce lifetime policy, or require fine-grained PAT approval. A pending token's successful public read proves little. GitHub CLI's privileged OAuth exception is a different policy surface. [S8] [S21] [S22] |
| `RuntimeNetworkUnproven` | Operator reads from a laptop do not establish runtime egress eligibility; GitHub IP allow lists can apply to App installation tokens and users. Validate the runtime path separately without relaxing restrictions. [S11] |
| `RuntimePermissionInsufficient` | "Resource not accessible by integration/personal access token" identifies a permission problem; compare endpoint requirements, installation grants and token downscoping. A 403 can also be rate limiting, not an owner-approval failure. [S26] |
| `RateLimitedOrTransient` | Preserve 403/429 distinction using `Retry-After` and rate-limit headers; follow documented waits rather than attempting credential escalation. [S26] |
| `ReadinessNotProvenByReadOnlyPreflight` | Metadata/membership/permission reads establish current evidence, not guaranteed future issue-write success, non-triggering of automation, continued approval, or complete evidence availability. No generic dry-run issue-creation endpoint is documented in the reviewed issue API. Do not create a "test issue" without separate authorization. [S19] [S26] [S45] |

A 200 public-repository read, `permissions.admin=true`, App registration permission declarations, an installation ID supplied by a browser, and a saved `repository_selection=all` string each prove **less** than "this council is authorized and ready." Match the intended App, actual account, installation grant, repository membership, granted permission version and selected runtime endpoints separately. [S5] [S6] [S7] [S18] [S35]

## 7. Current DSF prior art and implications

All paths here are relative to the isolated workspace
`/home/jbergfeld/vcs/dark-software-factory/.worktrees/research-onboard-github/`,
and links pin base `b427b762e7f5a9982b64059c995802d8b8d5920c`.

| Verified current implementation | Consequence for the later specification |
| --- | --- |
| [`GitHubProvisioningPlan.cs:30-72`](https://github.com/JoranBergfeld/dark-software-factory/blob/b427b762e7f5a9982b64059c995802d8b8d5920c/dotnet/src/Dsf.Cli/GitHubProvisioningPlan.cs#L30-L72) combines repository ensure, baseline CI, labels, App binding, protection, and optionally Creation retry workflow. | Not a read-only attach plan. Reuse of the entire greenfield sequence would violate this map's preservation boundary. |
| [`GitHubRestProvisioningClient.cs:40-51`](https://github.com/JoranBergfeld/dark-software-factory/blob/b427b762e7f5a9982b64059c995802d8b8d5920c/dotnet/src/Dsf.Cli/GitHubRestProvisioningClient.cs#L40-L51) reads `GH_TOKEN` / `GITHUB_TOKEN`; [`:124-150`](https://github.com/JoranBergfeld/dark-software-factory/blob/b427b762e7f5a9982b64059c995802d8b8d5920c/dotnet/src/Dsf.Cli/GitHubRestProvisioningClient.cs#L124-L150) can change an existing repository to private. | No current credential-store integration is established by that class; its "ensure" operation is not a safe discovery primitive. |
| [`GitHubRestProvisioningClient.cs:223-295`](https://github.com/JoranBergfeld/dark-software-factory/blob/b427b762e7f5a9982b64059c995802d8b8d5920c/dotnet/src/Dsf.Cli/GitHubRestProvisioningClient.cs#L223-L295) trusts a supplied `all` selection, otherwise reads `/user/installations/{id}/repositories` and conditionally calls the classic-PAT-only PUT. | Current code must not be taken as evidence that CLI OAuth supports those endpoints. Separate documented read modes, actual grant verification and consent routes in onboarding. [S5] |
| [`GitHubAppBootstrapClient.cs:17-42`](https://github.com/JoranBergfeld/dark-software-factory/blob/b427b762e7f5a9982b64059c995802d8b8d5920c/dotnet/src/Dsf.Cli/GitHubAppBootstrapClient.cs#L17-L42) creates a private App manifest with Issues, Pull requests, Contents and Administration **write**, submitting through personal settings. | This is broader than council read/file minima. Administration also defeats the conditional org-repository-admin install route; private personal ownership is a cross-account prerequisite, not merely another OAuth scope. [S6] [S19] [S34] |
| [`GitHubAppAuthProvider.cs:54-110`](https://github.com/JoranBergfeld/dark-software-factory/blob/b427b762e7f5a9982b64059c995802d8b8d5920c/dotnet/src/Dsf.Runtime/GitHubApp/GitHubAppAuthProvider.cs#L54-L110) reads an App private key, signs a JWT and mints/caches installation tokens; the request has no repository/permission downscope body. | Durable App identity already has .NET prior art, but narrower council authority is not already proven by this provider. [S16] |
| [`GitHubIssueFiler.cs:70-122`](https://github.com/JoranBergfeld/dark-software-factory/blob/b427b762e7f5a9982b64059c995802d8b8d5920c/dotnet/src/Dsf.Runtime/GitHubIssueFiler.cs#L70-L122) posts proposals with labels and optionally assigns a coding agent. | Filing and Creation activation need separate treatment for decide-only onboarding; preserving workflows alone does not prevent issue-driven automation. [S45] |

The current [.NET bootstrap guide](https://github.com/JoranBergfeld/dark-software-factory/blob/b427b762e7f5a9982b64059c995802d8b8d5920c/docs/site/get-started/bootstrap.md#L68-L85) already separates App creation, browser installation and credential storage. [ADR 0016](https://github.com/JoranBergfeld/dark-software-factory/blob/b427b762e7f5a9982b64059c995802d8b8d5920c/docs/adr/0016-creation-phase-coding-agent-reflection.md#L15-L40) supplies identity rationale, but its historical implementation language and description of `gh auth token` as a PAT are not current CLI authentication guarantees. No ADR or domain terminology was changed.

## 8. Remaining uncertainty and handoff

**Answered sufficiently for planning:** ordinary CLI OAuth versus installation-selection authority, documented browser/owner paths, enterprise exceptions, read-only discovery, and endpoint-specific runtime permission minima. No central conclusion depends on a live installation or on extracting a credential.

**Known limits:**

- Ordinary `/user/installations/...` GET support for CLI OAuth/classic PAT is not established by the reviewed references; cells remain ND. The add PUT's classic-PAT-only requirement is explicit. Do not infer support from local code or generic examples using `gh api`. [S5]
- The organization-installation inventory's `admin:read` scope is inconsistent with the scope catalogue. Resolve with GitHub if that optional route is selected; do not assume a corrected scope here. [S4] [S25]
- No live test was made of enterprise App-user-token authorization, organization policy combinations, pending permission upgrades, or issue-write interaction limits. The enterprise matrix records reference-listed support; the first-party tutorial demonstrates the installation-token route. [S14] [S15]
- GHES versions and enterprise-specific configurations were not exhaustively qualified. GitHub.com's ordinary and Enterprise Cloud APIs are separately evidenced; a GitHub.com App is not assumed portable to another host. [S2] [S34] [S36]
- Actual runtime networking, approved grants, approver identity, and the absence of issue-triggered delivery side effects require deployment/account-specific evidence or human agreement. They cannot be proven from public technology documentation. [S10] [S11] [S35] [S45]

**Existing-ticket refinements, not new decisions:** [Define repository additions without delivery takeover](https://github.com/JoranBergfeld/dark-software-factory/issues/196) already owns browser versus pre-existing-installation/PAT alternatives, allowed additions, and accidental automation; [Define council-only infrastructure and runtime permissions](https://github.com/JoranBergfeld/dark-software-factory/issues/191) owns App ownership/distribution, broad existing grants versus council minima, and runtime signing authority; [Define safe retry, completion, and offboarding](https://github.com/JoranBergfeld/dark-software-factory/issues/193) owns revalidation, pending approval, retry and offboarding; [Prototype the resource-selection and confirmation wizard](https://github.com/JoranBergfeld/dark-software-factory/issues/195) owns the handoff UX.

**Newly sharp compatibility question, not explicitly asked by an existing child:** "Should enterprise-delegated App installation management be a supported onboarding route, or an external prerequisite, given its ability to approve requests, accept permission upgrades, and unsuspend installations?" The parent can place this under [Define council-only infrastructure and runtime permissions](https://github.com/JoranBergfeld/dark-software-factory/issues/191)/[Define repository additions without delivery takeover](https://github.com/JoranBergfeld/dark-software-factory/issues/196) or add a focused compatibility ticket; this research does not select an answer. [S14] [S15]

Suggested named map-index gist: **GitHub authority boundary** - CLI discovery is not App consent; ordinary repository grants require browser/admin action or classic-PAT admin authority, with a separately delegated enterprise exception and installation-scoped runtime minima.

## Primary-source index

All live references below were read on 2026-09-14; anchors identify the relevant section or endpoint. Source-code links additionally pin revisions and line ranges.

[S1]: https://cli.github.com/manual/gh_auth_login
[S2]: https://cli.github.com/manual/gh_help_environment
[S3]: https://github.com/cli/cli/blob/38316c1c4f275030e3df6666382922e75410d68b/internal/authflow/flow.go#L27-L48
[S4]: https://docs.github.com/en/apps/oauth-apps/building-oauth-apps/scopes-for-oauth-apps
[S5]: https://docs.github.com/en/rest/apps/installations#add-a-repository-to-an-app-installation
[S6]: https://docs.github.com/en/apps/using-github-apps/installing-a-github-app-from-a-third-party#requirements-to-install-a-github-app
[S7]: https://docs.github.com/en/apps/using-github-apps/approving-updated-permissions-for-a-github-app
[S8]: https://docs.github.com/en/apps/oauth-apps/using-oauth-apps/privileged-oauth-apps
[S9]: https://docs.github.com/en/rest/authentication/authenticating-to-the-rest-api
[S10]: https://docs.github.com/en/enterprise-cloud@latest/apps/using-github-apps/saml-and-github-apps
[S11]: https://docs.github.com/en/enterprise-cloud@latest/organizations/keeping-your-organization-secure/managing-security-settings-for-your-organization/managing-allowed-ip-addresses-for-your-organization#about-allowed-ip-addresses
[S12]: https://docs.github.com/en/organizations/managing-programmatic-access-to-your-organization/limiting-oauth-app-and-github-app-access-requests-and-installations#about-github-app-installation-restrictions
[S13]: https://docs.github.com/en/apps/using-github-apps/reviewing-and-modifying-installed-github-apps
[S14]: https://docs.github.com/en/enterprise-cloud@latest/rest/enterprise-admin/organization-installations
[S15]: https://github.com/github/docs/blob/00f535f7c8a6428ef5377dc0672e7840886df37b/content/admin/managing-github-apps-for-your-enterprise/automate-installations.md#L26-L47
[S16]: https://docs.github.com/en/apps/creating-github-apps/authenticating-with-a-github-app/authenticating-as-a-github-app-installation
[S17]: https://docs.github.com/en/apps/creating-github-apps/authenticating-with-a-github-app/authenticating-with-a-github-app-on-behalf-of-a-user
[S18]: https://docs.github.com/en/rest/repos/repos#get-a-repository
[S19]: https://docs.github.com/en/rest/issues/issues#create-an-issue
[S20]: https://docs.github.com/en/rest/repos/contents
[S21]: https://docs.github.com/en/authentication/keeping-your-account-and-data-secure/managing-your-personal-access-tokens
[S22]: https://docs.github.com/en/organizations/managing-programmatic-access-to-your-organization/setting-a-personal-access-token-policy-for-your-organization
[S23]: https://docs.github.com/en/apps/creating-github-apps/registering-a-github-app/choosing-permissions-for-a-github-app
[S24]: https://docs.github.com/en/rest/apps/apps#get-a-repository-installation-for-the-authenticated-app
[S25]: https://docs.github.com/en/rest/orgs/orgs#list-app-installations-for-an-organization
[S26]: https://docs.github.com/en/rest/using-the-rest-api/troubleshooting-the-rest-api
[S27]: https://cli.github.com/manual/gh_api
[S28]: https://cli.github.com/manual/gh_auth_status
[S29]: https://cli.github.com/manual/gh_repo_view
[S30]: https://docs.github.com/en/rest/collaborators/collaborators#get-repository-permissions-for-a-user
[S31]: https://docs.github.com/en/rest/repos/rules
[S32]: https://docs.github.com/en/rest/branches/branch-protection#get-branch-protection
[S33]: https://docs.github.com/en/rest/interactions/repos#get-interaction-restrictions-for-a-repository
[S34]: https://docs.github.com/en/apps/creating-github-apps/registering-a-github-app/making-a-github-app-public-or-private
[S35]: https://docs.github.com/en/apps/creating-github-apps/registering-a-github-app/about-the-setup-url
[S36]: https://docs.github.com/en/rest/about-the-rest-api/api-versions
[S37]: https://github.com/github/rest-api-description/blob/e16cc2584c6d32e8cd4d461759e1475e79327d6c/descriptions/api.github.com/api.github.com.json#L40156-L40230
[S38]: https://github.com/github/rest-api-description/blob/e16cc2584c6d32e8cd4d461759e1475e79327d6c/descriptions/ghec/ghec.json#L9963-L9985
[S39]: https://docs.github.com/en/rest/issues/labels
[S40]: https://docs.github.com/en/rest/pulls/pulls
[S41]: https://docs.github.com/en/rest/actions/workflow-runs
[S42]: https://docs.github.com/en/rest/deployments/deployments#list-deployments
[S43]: https://docs.github.com/en/rest/checks/runs#list-check-runs-for-a-git-reference
[S44]: https://docs.github.com/en/rest/commits/statuses#get-the-combined-status-for-a-specific-reference
[S45]: https://docs.github.com/en/actions/how-tos/write-workflows/choose-when-workflows-run/trigger-a-workflow#triggering-a-workflow-from-a-workflow
[S46]: https://docs.github.com/en/repositories/archiving-a-github-repository/archiving-repositories#about-repository-archival
[S47]: https://github.com/cli/cli/blob/38316c1c4f275030e3df6666382922e75410d68b/pkg/cmd/auth/shared/oauth_scopes.go#L72-L79
