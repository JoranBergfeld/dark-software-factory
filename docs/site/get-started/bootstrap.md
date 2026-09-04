# Bootstrap the owner GitHub App

`dsf bootstrap` is the one-time owner setup step that creates the DSF GitHub App, discovers
its installation id, creates the owner Key Vault and App Configuration store, and stores the
App id, installation id, and private key for later `dsf new` and charter operations.

## Prerequisites

- `dsf` installed from the packaged global tool or self-contained release archive.
- `gh auth login` completed for the owner account or organization.
- `az login` completed against the target subscription.
- Permission to create a GitHub App and Azure resource groups, Key Vaults, App Configuration
  stores, role assignments, and deployments.

## Run bootstrap

```bash
dsf bootstrap \
  --app-name dsf-<owner> \
  --resource-group rg-dsf-owner \
  --keyvault-name kv-dsf-owner \
  --appconfig-name cfg-dsf-owner
```

The command:

1. opens the GitHub App manifest flow,
2. exchanges the one-time manifest code for permanent App credentials,
3. discovers the installation id,
4. creates or updates the owner Key Vault with purge protection and soft delete,
5. creates or updates the owner App Configuration store,
6. stores the three GitHub App secrets in Key Vault.

If the owner Key Vault already contains all three secrets, bootstrap skips the GitHub App
exchange.

## WSL and headless environments

`dsf bootstrap` prints the local manifest HTML path and waits for the localhost callback. If
the browser does not open, or the redirect to `http://127.0.0.1:8765/callback` never reaches
the shell, copy the `?code=...` value from the redirect URL and paste it back into the
terminal when prompted.

## Recovery if bootstrap fails mid-run

After the manifest exchange succeeds, DSF writes a recovery file under the operator's DSF
state directory. It contains the App id, installation id, and private key, is created with
owner-only permissions, and is deleted after the Key Vault secrets are stored.

If Azure policy, RBAC propagation, or Key Vault creation fails after the GitHub step:

1. keep the GitHub App,
2. fix the Azure-side problem,
3. rerun `dsf bootstrap` with the same `--app-name`.

DSF detects the recovery file and resumes from the saved credentials instead of replaying the
one-time GitHub manifest exchange.

## Manual fallback: seed the owner Key Vault yourself

If you need manual recovery, create the owner vault with an ARM deployment that explicitly
sets soft delete and purge protection. Store the template as `dsf-owner-kv.json` in your
current working directory, deploy it, grant yourself `Key Vault Secrets Officer`, then seed
these secrets:

```bash
az keyvault secret set --vault-name kv-dsf-owner --name github-app-id --value '<app-id>' -o none
az keyvault secret set --vault-name kv-dsf-owner --name github-app-installation-id --value '<installation-id>' -o none
az keyvault secret set --vault-name kv-dsf-owner --name github-app-private-key --file ./app-private-key.pem -o none
```

After manual seeding, rerun `dsf bootstrap`; it detects the existing secrets and exits without
recreating the GitHub App.
