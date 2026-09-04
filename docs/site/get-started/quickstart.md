# Quickstart

Dark Software Factory (DSF) is the **blueprint**, not a running factory. Install the packaged
CLI once, then stamp out an isolated factory per product with `dsf new`.

For the big picture, read [The loop](../concept/the-loop.md) and
[The harness](../concept/the-harness.md). For design history, see the
[ADRs](https://github.com/JoranBergfeld/dark-software-factory/tree/main/docs/adr).

## Prerequisites

- [.NET SDK](https://dotnet.microsoft.com/download) matching `dotnet/global.json` for
  contributor builds.
- The [GitHub CLI](https://cli.github.com/) (`gh`), authenticated with `gh auth login`, for
  repository ownership and GitHub release access.
- The [Azure CLI](https://learn.microsoft.com/cli/azure/) (`az`) logged in to the target
  subscription for real provisioning.
- The packaged DSF CLI, installed as a global tool or from a self-contained release archive.

## Install DSF

Global tool install:

```bash
dotnet tool install --global DarkSoftwareFactory.Cli
```

Pinned install:

```bash
dotnet tool install --global DarkSoftwareFactory.Cli --version <version>
```

Self-contained install:

1. Download the matching GitHub Release archive: `dsf-cli-linux-x64.tar.gz`,
   `dsf-cli-linux-arm64.tar.gz`, `dsf-cli-osx-x64.tar.gz`, `dsf-cli-osx-arm64.tar.gz`,
   `dsf-cli-win-x64.zip`, or `dsf-cli-win-arm64.zip`.
2. Verify it with [Verify a release](verify-release.md).
3. Extract it and put the extracted directory on `PATH`.
4. Run `dsf --help`.

## Verify a checkout

Contributors validate the active implementation from the .NET workspace:

```bash
cd dotnet
dotnet restore Dsf.sln --locked-mode
dotnet build Dsf.sln --no-restore
dotnet test Dsf.sln --no-build
```

## Owner bootstrap unavailable

`dsf bootstrap` is **not implemented** in the current .NET CLI. It exits successfully without
provisioning a GitHub App, owner Key Vault, App Configuration, or credentials. Configure that
owner infrastructure outside DSF before using product provisioning.

For an already configured owner, export the owner endpoints:

```bash
export DSF_OWNER_KEYVAULT_URI=https://<owner-keyvault>.vault.azure.net/
export DSF_OWNER_APPCONFIG_ENDPOINT=https://<owner-appconfig>.azconfig.io
```

The stores must also contain the GitHub App and source-agent credentials required by `dsf new`.
Next: [provision a factory](provision-a-factory.md).
