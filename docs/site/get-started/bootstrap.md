# Bootstrap the owner GitHub App

!!! warning "Unavailable in the .NET CLI"
    `dsf bootstrap` is **not implemented** in the current .NET CLI. It prints a migration-shell
    notice and exits successfully without creating a GitHub App, Azure resources, credentials,
    or recovery state. Do not use it as an owner-infrastructure provisioning step.

## Current path

Configure the owner GitHub App, Key Vault, App Configuration store, and required credentials
outside the current DSF CLI. After those owner services exist, export their endpoints:

```bash
export DSF_OWNER_KEYVAULT_URI=https://<owner-keyvault>.vault.azure.net/
export DSF_OWNER_APPCONFIG_ENDPOINT=https://<owner-appconfig>.azconfig.io
```

Then use the packaged `dsf` release to [provision a factory](provision-a-factory.md) with
`dsf new`. The owner endpoints alone are not credentials; the configured owner stores must
contain the GitHub App and source-agent secrets required by that workflow.
