# Review an existing repository for Decide

`dsf onboard decide review` performs a **read-only attachment assessment** of an
existing GitHub.com repository. It does not onboard an application, reserve a
factory, provision Azure resources, create a charter PR, or authorize publication.
`dsf new` remains the separate greenfield workflow.

To combine this fresh assessment with an exact Azure application selection, use
[preview a dedicated application boundary](preview-application-boundary.md).

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

Workflow activity filters are retained in evidence, including YAML flow and block
forms. Review considers proposal issue `opened`/`labeled` events (the issue is
created unassigned with `dsf:proposal`), charter PR `opened` events and its branch
`create`/`push` events, and label-definition `created` events. Default subscriptions
include the applicable creation events. Filters limited to later human activities,
such as closing or assigning an issue, closing a PR, or editing/deleting a label,
do not block creation. Comments and reviews are not part of these DSF mutations.
Unknown activities, malformed filters, and unsupported event/filter combinations
remain explicitly unknown rather than being ignored or called known triggers.

Directly recognized delivery/coding-agent commands in relevant workflow `run`
steps block the affected mutation class. Workflow/job/step names, descriptive
values, and printed literals are not executable delivery evidence. Recognition is
deliberately limited to simple commands in standard shells; custom shells,
compound scripts, reusable actions/workflows, and downstream dispatch can remain
unknown. Other event filters, such as branch/path constraints, are not proved by
this activity-filter review.

Delivery-step eligibility is checked separately for each subscribed event. Both
job and step `if` guards must be absent or established true. The bounded guard
reader understands constant `true`/`false` and `github.event_name` equality or
inequality against a literal event name, including `${{ ... }}` wrappers.
False or unproven guards do not establish known delivery; unsupported expressions
are never evaluated. Excluding a command does not prove all workflow effects safe:
without an eligible known instruction, the applicable review can remain `Unknown`.

For `npm publish`, explicit `--dry-run=false` (or `--dry-run false`) disables dry
run and retains the known publishing block. Recognized boolean flags, including
`--no-dry-run`, apply in order. Enabled dry runs and unrecognized flag values or
expressions remain `Unknown`, not proof that arbitrary workflow effects are harmless.

For `gh pr merge`, enabled `--disable-auto` disables auto-merge rather than merging,
so it does not establish known delivery. Explicit `--disable-auto=false` retains
the known merge instruction. GitHub CLI boolean literals and repeated flags are
handled separately from npm: bare `--disable-auto` is true, and a following
`false` is a positional argument, not its value. Unsupported arguments or boolean
values remain `Unknown`; disabling auto-merge is not claimed to be read-only or
proof that the workflow has no other effects.

A `clear` declaration cannot override a known trigger, incomplete inspection, or
unknown downstream effects. Generic CI handlers can therefore remain unknown:
the scanner does not prove arbitrary scripts or reusable actions harmless.
The external owner must reconcile the finding outside DSF; if the visible effects
remain opaque, repeating a declaration does not clear the assessment. There is no
force option and no automatic repair. Even a confirmed assessment is only a current,
limited review, not exhaustive technical proof or permission to mutate.

## Labels: inspect, never change

Only these four names are read:

| Label | Required meaning / canonical description |
| --- | --- |
| `dsf:proposal` | DSF proposal for human consideration; not a delivery assignment. |
| `dsf-outcome:approved` | Human-approved DSF proposal; not a delivery assignment. |
| `dsf-outcome:rejected` | Human-rejected DSF proposal. |
| `dsf-outcome:changes-requested` | DSF proposal requiring human-requested changes. |

Absent labels are recorded without creation. Existing IDs, colors, and descriptions
are preserved. Conservative description matching ignores case, whitespace,
sentence punctuation (`.`, `,`, `;`, `:`), and hyphenation; presentation-only
variations of the required meaning establish compatibility without rewriting the
description. Missing descriptions and unrecognized wording remain `Unknown`, not
asserted semantic conflicts. Descriptions matching a different allowed label's
canonical meaning, including presentation-only variants, are known conflicts and
are `Blocked`, as are recognized affirmative delivery/assignment meanings.
Both unresolved meanings and known conflicts prevent reuse pending
external reconciliation; no model guesses at arbitrary wording. A compatible
description does not clear conflicting automation. Review offers no label-edit or
semantic-override option.

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
