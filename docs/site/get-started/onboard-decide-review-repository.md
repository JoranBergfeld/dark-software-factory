# Review a repository for Decide onboarding

**This is a read-only assessment.** `dsf onboard decide review-repository` never writes to
GitHub, never creates a replacement repository, and never implies that being able to see a
repository is the same as consenting to install the DSF GitHub App on it.

## What it checks

Given `--repo <owner>/<name>`, the command uses your effective GitHub CLI authentication to:

- Resolve the repository's durable ID, observed owner/name, and actual default branch — it
  never infers these from a product key or assumes `main`.
- Reject archived repositories or repositories with issues disabled, without unarchiving,
  enabling issues, or otherwise mutating the repository to make it compatible.
- Inspect the prepared owner control plane (Key Vault + App Configuration, if reachable) and
  the DSF GitHub App's installation to distinguish registration, installation
  suspension, selected-repository membership, and granted permissions. Anything that cannot be
  verified is reported as `Unknown` with the responsible next actor — never assumed granted.
- Review the four permitted label names (`dsf:proposal`, `dsf-outcome:approved`,
  `dsf-outcome:rejected`, `dsf-outcome:changes-requested`) for semantic conflicts, without
  creating or editing any label.
- Detect known issue/PR/label-triggered workflows and webhooks that could interfere with
  Decide's mutation classes, and separately record the operator's own automation declaration
  (a declaration is never treated as technical proof).

## Prerequisites

- A GitHub API token that can read the repository:

  ```bash
  export GH_TOKEN="$(gh auth token)"
  ```

  `GITHUB_TOKEN` is also supported.
- Optionally, `--owner-keyvault-uri` and `--owner-appconfig-endpoint` to check the owner
  control plane created by [`dsf bootstrap`](bootstrap.md). Without these, App
  registration/installation prerequisites are reported as `Unknown` rather than guessed.

## Usage

```bash
dsf onboard decide review-repository --repo acme/widgets
```

Omit `--repo` in an interactive terminal to be prompted for it; in a non-interactive or
redirected session, `--repo` is required and the command fails loudly if it is missing.

The command prints the repository identity, each prerequisite's state (`Confirmed`,
`Pending`, `Denied`, or `Unknown`) with its responsible next actor, the label review, any
automation findings, and the overall eligibility (`Eligible`, `Blocked`, or `Unknown`) with
blocking reasons. The exit code reflects whether the assessment itself completed, not whether
the repository is eligible — a `Blocked` or `Unknown` result is still a successful, honest
assessment.

This command is the first slice of `dsf onboard decide`: it produces the typed
repository-review facts that later attachment planning consumes. It does not attach a
Feature Council, provision Azure resources, or write any labels, issues, or webhooks.
