# Bootstrap the owner GitHub App

!!! warning "Unavailable in the .NET CLI"
    `dsf bootstrap` is **not implemented** in the current .NET CLI. It prints a migration-shell
    notice and exits successfully without creating a GitHub App, Azure resources, credentials,
    or recovery state. Do not use it as an owner-infrastructure provisioning step.

## Current path

Configure any owner GitHub App, Key Vault, and App Configuration services outside the current
DSF CLI. The CLI does not retrieve or seed owner-vault credentials for `dsf new`; supply GitHub
credentials and GitHub App identifiers through the command's documented options or environment
variables. After those owner services exist, export the App Configuration endpoint required to
publish the product index:

```bash
export DSF_OWNER_APPCONFIG_ENDPOINT=https://<owner-appconfig>.azconfig.io
```

Then use the packaged `dsf` release to [provision a factory](provision-a-factory.md) with
`dsf new`. Owner endpoints identify configuration services; they do not supply credentials to the
CLI.
