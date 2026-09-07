# Bootstrap the owner control plane

`dsf bootstrap` creates the reusable owner App Configuration store, owner Key Vault,
and DSF GitHub App required before provisioning product factories.

## Prerequisites

- `az login` completed in the target subscription with permission to create resource
  groups, role assignments, App Configuration, and Key Vault resources.
- `gh auth login` completed for the GitHub account or organization that owns product
  repositories.
- Unique, Azure-valid names for the Key Vault and App Configuration store.

## Preview

```bash
dsf bootstrap \
  --app-name dsf-sbx-20260907 \
  --resource-group rg-dsf-app \
  --keyvault-name kvdsfsbx20260907 \
  --appconfig-name appcsdsfsbx20260907 \
  --location swedencentral \
  --dry-run
```

The preview performs no Azure or GitHub mutations.

## Create the owner

Run the same command without `--dry-run`. DSF confirms before creating Azure services
and again before creating the GitHub App. Use `--yes` only for intentional automation.

Bootstrap creates App Configuration first, then records non-secret stage status at
`dsf/owner/bootstrap/<app-name>/status`. It creates an RBAC-enabled Key Vault with
purge protection, grants the operator required data-plane roles, creates a private
GitHub App with selected-repositories scope, and stores its App ID, installation ID,
and private key in Key Vault. Secrets are never stored in App Configuration.

After GitHub opens the App Manifest page, create and install the App for **selected
repositories**, then paste the resulting callback URL or its `code` parameter into
the terminal. This works in headless and WSL environments too.

On success, export the printed endpoints:

```bash
export DSF_OWNER_KEYVAULT_URI=https://<owner-keyvault>.vault.azure.net/
export DSF_OWNER_APPCONFIG_ENDPOINT=https://<owner-appconfig>.azconfig.io
```

Use them for [product provisioning](provision-a-factory.md).
