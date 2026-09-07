# Design: restore durable owner bootstrap to `dsf`

- Status: Approved
- Date: 2026-09-07
- Scope: owner control-plane setup and the credential path required to provision one
  functioning product factory.

## Problem

The .NET CLI exposes `dsf bootstrap` with required owner App, Key Vault, and App
Configuration names, but the command is a successful no-op. Consequently, a new
operator has no supported way to create the owner control plane required by `dsf
new`, and an apparently successful product deployment lacks the GitHub App private
key its runtime needs.

The former Python implementation created the owner GitHub App through GitHub's App
Manifest flow, created Azure owner services, and persisted GitHub credentials in
Key Vault. The .NET implementation must restore this behavior without redesigning
the existing `bootstrap` command's flags or the default single owner resource group.

## Goals

- Make `dsf bootstrap` provision a reusable owner control plane.
- Make bootstrap progress remotely inspectable at every stage.
- Keep secrets exclusively in Key Vault and make secrets durable before they can be
  lost from the operator's machine.
- Make `dsf new` consume bootstrapped App identity automatically and provision a
  runtime that can authenticate to GitHub.
- Prove the path with a live Sandbox owner bootstrap and one private throwaway
  product factory.

## Non-goals

- No private-endpoint topology or alternate owner-resource layout.
- No webhooks, event receiver, or expanded GitHub App permissions.
- No automatic modification of shell profiles or `.env` files.
- No automatic cross-workstation transfer of secrets before Key Vault accepts them.

## Command contract

Keep these existing required options and defaults:

```text
dsf bootstrap
  --app-name <GitHub App name>
  --keyvault-name <owner Key Vault name>
  --appconfig-name <owner App Configuration store name>
  [--resource-group rg-dsf-app]
  [--location swedencentral]
```

Add safety-only options:

- `--dry-run`: render the complete plan without Azure, GitHub, Key Vault, or App
  Configuration mutations.
- `--yes`: approve both mutation confirmation gates for deliberate automation.

Without `--yes`, the command asks for confirmation before owner Azure provisioning
and again before opening GitHub's irreversible App Manifest conversion. On success,
it prints copyable exports for `DSF_OWNER_KEYVAULT_URI` and
`DSF_OWNER_APPCONFIG_ENDPOINT`; it does not write local configuration.

## Architecture

Add a focused owner-bootstrap module in `Dsf.Cli`, composed by the existing command
handler. Its independent responsibilities are:

| Component | Responsibility |
|---|---|
| Owner Azure provisioner | Ensure the resource group, Standard App Configuration with local authentication disabled, RBAC Key Vault with soft delete and purge protection, and the operator's data-plane roles. |
| Bootstrap status store | Write one non-secret status document for the App name to owner App Configuration. |
| GitHub App bootstrapper | Render the least-privilege manifest, run the browser/local-callback exchange, accept pasted callback codes, and discover the selected-repositories installation. |
| Owner credential store | Store and read the App ID, installation ID, and PEM in the owner Key Vault without echoing secret values. |
| Owner credential resolver | Let `dsf new` and charter operations resolve the owner App identity from the bootstrapped services. |

The CLI remains responsible only for parsing options, rendering/confirming the plan,
and reporting outcomes. Azure and GitHub calls use narrow injectable seams so their
argument shape, ordering, retries, and failure behavior can be tested without live
services.

## Durable bootstrap flow

1. Render the full plan. `--dry-run` stops here; otherwise obtain the Azure
   confirmation.
2. Ensure the configured resource group.
3. Create or reconcile the owner Standard App Configuration store with local
   authentication disabled. Grant the executing operator `App Configuration Data
   Owner`.
4. Create or reconcile the RBAC-enabled owner Key Vault with 90-day soft delete and
   purge protection. Grant the executing operator `Key Vault Secrets Officer`.
5. Initialize and update
   `dsf/owner/bootstrap/<app-name>/status` in App Configuration. The document
   contains the current and completed stages, timestamps, owner resource pointers,
   and sanitized failure information. App Configuration revisions provide history.
6. Obtain the GitHub confirmation, submit GitHub's App Manifest, capture the local
   redirect code or accept a pasted code, exchange it, and discover the installation.
   The App is private, has selected-repositories scope, requests write access only
   to issues, pull requests, contents, and administration, and declares no webhook
   endpoint or events.
7. Immediately store `github-app-id`, `github-app-installation-id`, and
   `github-app-private-key` in the owner Key Vault. Secret writes are retried for
   role-propagation delays and never print secret bundles.
8. Mark status complete and print the two endpoint exports.

Every stage persists non-secret outputs and progress in the status document. Any
secret produced by a stage is written to Key Vault immediately and never enters App
Configuration, logs, the terminal, a shell profile, or a repository `.env` file.

## Reconciliation and failure behavior

Azure resource creation and role assignments are idempotent. If every required App
secret already exists, bootstrap verifies the existing identity and completes its
status record without creating a second GitHub App. If only some credentials exist,
the command fails with the missing secret names; it never silently overwrites or
mixes App identities.

The browser flow supports a localhost callback and pasted callback URL/code for
headless and WSL use. If a failure occurs after the manifest conversion but before
Key Vault storage, protected local recovery state contains only the credentials
needed to retry storage. It is removed immediately after success. Once Key Vault
accepts credentials, remote services are sufficient for subsequent operations.

Status records include a sanitized error summary, never command output that might
contain a secret.

## Product-provisioning integration

`dsf new` reads the owner configuration specified by the two endpoint exports. It
automatically resolves the App ID and installation ID instead of requiring those
identifiers for every invocation. It reads the owner PEM through Azure-authenticated
Key Vault access and copies it into the per-product Key Vault as
`github-app-private-key`, so the deployed runtime's existing
`GITHUB_APP_PRIVATE_KEY_SECRET` setting resolves correctly. Charter operations use
the owner Key Vault credential path to authenticate as the same App.

This restores a functional path: bootstrap owner -> provision product -> runtime and
charter GitHub authentication.

## Testing and documentation

Add deterministic tests for:

- the preserved command grammar plus `--dry-run` and `--yes`;
- plan rendering and both confirmation gates;
- Azure command construction, status transitions, role-propagation retries, and
  idempotent reconciliation;
- App Manifest permissions, callback parsing, and selected-repositories installation;
- Key Vault secret redaction, cleanup, complete credential reuse, and partial
  credential failure;
- automatic owner credential resolution, product-key propagation, and charter
  authentication;
- updated operator documentation.

Update the README and bootstrap, quickstart, and factory-provisioning guides so they
describe the shipped behavior and its Azure/GitHub prerequisites.

## Live acceptance

After implementation and deterministic validation:

1. Run a final `dsf bootstrap --dry-run` plan.
2. On separate confirmation, create the owner services in the Sandbox subscription
   using `dsf-sbx-20260907` naming.
3. Run `dsf new --dry-run` for private
   `dsf-sbx-probe-20260907`, review its plan, then separately confirm live
   provisioning.
4. Inspect the remote bootstrap status and product registration.
5. Retain the owner App, Key Vault, and App Configuration as the reusable control
   plane. After inspection, explicitly confirm deletion of the throwaway product
   resource group and private repository to prevent ongoing cost.
