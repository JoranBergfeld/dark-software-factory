# Bootstrap the owner GitHub App

`dsf bootstrap` is the one-time owner setup step that creates the DSF GitHub App,
discovers its installation id, creates the owner Key Vault, and stores the App id,
installation id, and private key there for later `dsf new` and charter operations.

## Prerequisites

- `uv` installed; run DSF commands through `uv run`.
- `gh auth login` completed for the owner account/org.
- `az login` completed against the target subscription.
- Permission to create a GitHub App and to create Azure resource groups, Key Vaults,
  role assignments, and deployments.

## Run bootstrap

```bash
uv run dsf bootstrap \
  --app-name dsf-<owner> \
  --resource-group rg-dsf-owner \
  --keyvault-name kv-dsf-owner
```

The command:

1. opens the GitHub App manifest flow,
2. exchanges the one-time manifest code for permanent App credentials,
3. discovers the installation id,
4. creates or updates the owner Key Vault with purge protection + soft delete,
5. stores the three secrets in Key Vault.

If the owner Key Vault already contains all three secrets, bootstrap is skipped.

## WSL and headless environments

`dsf bootstrap` prints the local manifest HTML path and waits for the localhost callback.
If the browser does not open, or the redirect to `http://127.0.0.1:8765/callback` never
reaches the Linux process (common on WSL), copy the `?code=...` value from the redirect
URL and paste it back into the terminal when prompted.

## Recovery if bootstrap fails mid-run

After the manifest exchange succeeds, DSF writes a recovery file to:

```text
~/.dsf/bootstrap-<app-name>.recovery.json
```

It contains the App id, installation id, and private key, is created with mode `0600`,
and is deleted automatically after the Key Vault secrets are stored successfully.

If Azure policy, RBAC propagation, or Key Vault creation fails after the GitHub step:

1. keep the GitHub App,
2. fix the Azure-side problem,
3. rerun `dsf bootstrap` with the same `--app-name`.

DSF detects the recovery file and resumes from the saved credentials instead of trying to
replay the one-time GitHub manifest exchange.

## Manual fallback: create and seed the owner Key Vault yourself

If you need to recover manually, create the owner vault with an ARM deployment that
explicitly sets soft delete and purge protection:

```bash
az group create --name rg-dsf-owner --location swedencentral

# Write the template to a file to avoid shell-quoting issues.
cat > /tmp/dsf-owner-kv.json << 'EOF'
{
  "$schema": "https://schema.management.azure.com/schemas/2019-04-01/deploymentTemplate.json#",
  "contentVersion": "1.0.0.0",
  "parameters": {
    "vaultName": {"type": "string"},
    "location":  {"type": "string"}
  },
  "resources": [{
    "type": "Microsoft.KeyVault/vaults",
    "apiVersion": "2022-07-01",
    "name": "[parameters('vaultName')]",
    "location": "[parameters('location')]",
    "properties": {
      "sku": {"family": "A", "name": "standard"},
      "tenantId": "[subscription().tenantId]",
      "enableRbacAuthorization": true,
      "enableSoftDelete": true,
      "enablePurgeProtection": true,
      "softDeleteRetentionInDays": 90,
      "accessPolicies": []
    }
  }]
}
EOF

az deployment group create \
  --resource-group rg-dsf-owner \
  --name dsf-owner-kv-kv-dsf-owner \
  --template-file /tmp/dsf-owner-kv.json \
  --parameters vaultName=kv-dsf-owner location=swedencentral
```

Grant yourself `Key Vault Secrets Officer`, then seed the three secrets from the recovery
file values:

```bash
az keyvault secret set --vault-name kv-dsf-owner --name github-app-id --value '<app-id>' -o none
az keyvault secret set --vault-name kv-dsf-owner --name github-app-installation-id --value '<installation-id>' -o none
az keyvault secret set --vault-name kv-dsf-owner --name github-app-private-key --file ~/.dsf/app-private-key.pem -o none
```

After manual seeding, rerun `dsf bootstrap`; it will detect the existing secrets and exit
without recreating the GitHub App.
