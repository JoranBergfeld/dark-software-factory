# Review an existing repository for Decide

`dsf onboard decide review` performs a **read-only attachment assessment** of an
existing GitHub.com repository. It does not onboard an application, reserve a
factory, provision Azure resources, create a charter PR, or authorize publication.
`dsf new` remains the separate greenfield workflow.

## Authentication and prerequisites

Repository discovery uses **effective GitHub CLI authentication**, including the
account selected by `gh` and any environment credential that `gh` gives precedence.
Check `gh auth status --hostname github.com`; no token export or broader token is
required by this command. DSF records the effective account's ID/login, repository
ID, observed owner/name, actual default branch, visibility, and relevant settings.
It rechecks repository metadata before returning; drift blocks the mixed review
rather than retargeting it.

Use the [prepared owner control plane](bootstrap.md), not a factory Key Vault:

```bash
export DSF_OWNER_APPCONFIG_ENDPOINT=https://<owner-appconfig>.azconfig.io
export DSF_OWNER_KEYVAULT_URI=https://<owner-keyvault>.vault.azure.net/
```

The Azure CLI must already have authorized access to that owner's App Configuration
status and Key Vault secrets. Endpoints must be public-cloud HTTPS service endpoints,
without embedded credentials, query parameters, or extra paths.

DSF reads the unlabelled `dsf/owner/bootstrap/*/status` records through `az` login,
requires one completed identity with matching owner endpoints, then reads
`github-app-id`, `github-app-installation-id`, and `github-app-private-key` from
the existing owner vault. It verifies the registered App, exact installation
ID/account, selected-repository membership, permission grants, current permission
approval, and suspension through GitHub's App-authenticated read APIs.

The owner key signs an in-memory App JWT for `GET /app`,
`GET /app/installations/{installation_id}`, and
`GET /repos/{owner}/{repo}/installation`. No installation token is minted and no
factory key copy is needed. This is **not factory-runtime access verification**.
The reused owner signing key can mint broader App-authorized tokens; that accepted
exposure is not eliminated by this review. No operator credential, JWT, private key,
or workflow source is persisted or printed.

Missing/incomplete owner status remains pending or unknown. Unreadable policy,
SSO eligibility, or installation metadata never counts as consent. The report
identifies the owner administrator, installation account administrator, repository
administrator, or automation owner who must act. Review never bootstraps, expands
an installation, upgrades permissions, unsuspends access, enables issues,
unarchives a repository, or creates a replacement.

## Interactive review

```bash
dsf onboard decide review
```

An interactive terminal asks for the repository, missing owner endpoints, and
per-mutation automation declarations. Blank owner endpoints or declarations remain
unknown; they do not choose defaults or authorize changes. Explicit options bypass
their corresponding prompts. The result includes an equivalent explicit command.

## Redirected / explicit-input review

```bash
dsf onboard decide review \
  --repo acme/existing-application \
  --owner-appconfig-endpoint https://owner.azconfig.io \
  --owner-keyvault-uri https://owner.vault.azure.net/ \
  --issue-automation unknown \
  --pr-automation unknown \
  --label-automation unknown \
  --json > attachment-review.json
```

`--json` disables prompts, even on an interactive terminal. In any redirected
invocation, `--repo OWNER/REPO` is required. Owner endpoints may come from the two
environment variables; absent endpoints produce a non-success assessment, not
greenfield provisioning. DSF itself writes no assessment file: shell redirection
above is an explicit operator choice.

After the accountable automation owner has reviewed each mutation class, replace
its `unknown` with `clear` or `conflict` and supply
`--automation-owner acme/platform`. These are **operator declarations attributed to
the effective GitHub identity**, not proof of every external bot or webhook.
Do not put credentials or confidential source into an owner/team identifier.

| Option | Reviewed mutation class |
| --- | --- |
| `--issue-automation` | Creating a proposal issue with the proposal label |
| `--pr-automation` | Creating a charter PR, including branch/push-triggered automation |
| `--label-automation` | Creating the four allowed label definitions |

The GitHub CLI reads Actions workflow inventory, active workflow files at the
observed default branch, and repository webhook subscriptions. Workflow references
include their observed blob SHA; raw source and webhook URLs/configuration are
withheld. Inventory is paginated, capped at 1,000 workflows/hooks, with a bounded
workflow-file read. Truncation, inaccessible inventory, malformed data, and
unresolved relevant webhook or transitive/scripted effects remain unknown.

Known delivery/coding-agent instructions in relevant event handlers block the
affected mutation class. A `clear` declaration cannot override a known trigger,
incomplete inspection, or unknown downstream effects. Generic CI handlers can
therefore remain unknown: the scanner does not prove arbitrary scripts or reusable
actions harmless. The external owner must reconcile the finding outside DSF.
There is no force option and no automatic repair. Even a confirmed assessment is
only a current, limited review, not exhaustive technical proof or permission to mutate.

## Labels: inspect, never change

Only these four names are read:

| Label | Required meaning / canonical description |
| --- | --- |
| `dsf:proposal` | DSF proposal for human consideration; not a delivery assignment. |
| `dsf-outcome:approved` | Human-approved DSF proposal; not a delivery assignment. |
| `dsf-outcome:rejected` | Human-rejected DSF proposal. |
| `dsf-outcome:changes-requested` | DSF proposal requiring human-requested changes. |

Absent labels are recorded without creation. Existing IDs, colors, and descriptions
are preserved. This first review uses conservative description matching:
case-insensitive canonical descriptions establish compatibility; missing descriptions
remain unknown, and other descriptions block automatic reuse pending external
semantic reconciliation. A matching description does not clear conflicting
automation. Review offers no label-edit or semantic-override option.

## Outcomes and reuse

Plain text and JSON expose per-check `Confirmed`, `Pending`, `Unknown`, `Denied`,
and `Blocked` findings with next actors/actions. Overall precedence is blocked
(including denied), unknown, pending, then confirmed.

| Exit | Meaning |
| --- | --- |
| `0` | Assessment confirmed within its stated limits; **not onboarded** |
| `1` | Pending, unknown, denied, blocked, invalid required input, or execution failure |
| `2` | Command-line parsing error |
| `130` | Canceled |

JSON is a version-1 `Dsf.Core.Onboarding.RepositoryAttachmentReview`: observation
time, durable repository facts, operator, observed App/installation, four label
reviews, three mutation-class reviews, and actionable findings. It is a local
projection, not an owner reservation or authoritative factory manifest. Subsequent
planning can consume these typed facts, but later operations must revalidate
identity, access, configuration, and automation rather than treating saved
`Confirmed` as current authorization.
