# Quickstart

Dark Software Factory (DSF) is the **blueprint**, not a running factory. Install the packaged
CLI once, then stamp out an isolated factory per product with `dsf new`.

For the big picture, read [The loop](../concept/the-loop.md) and
[The harness](../concept/the-harness.md). For design history, see the
[ADRs](https://github.com/JoranBergfeld/dark-software-factory/tree/main/docs/adr).

## Prerequisites

- A GitHub API token that can create repositories under the selected owner:

  ```bash
  export GH_TOKEN="$(gh auth token)"
  ```

  `GITHUB_TOKEN` is also supported. `dsf new` uses this environment credential; it does not
  retrieve GitHub credentials from `gh` or an owner Key Vault.
- The [Azure CLI](https://learn.microsoft.com/cli/azure/) (`az`) logged in to the target
  subscription for real provisioning.
- The packaged DSF CLI, installed as a global tool or from a self-contained release archive.
- The [.NET SDK](https://dotnet.microsoft.com/download) (10.0 or later) **only** if you install
  the global tool with `dotnet tool install`. The self-contained release archives bundle their
  own runtime and need no .NET SDK.

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

## Owner bootstrap unavailable

`dsf bootstrap` is **not implemented** in the current .NET CLI. It exits successfully without
provisioning a GitHub App, owner Key Vault, App Configuration, or credentials. Configure that
owner infrastructure outside DSF before using product provisioning. `dsf new` does not retrieve
or seed credentials from configured owner stores; provide its GitHub credential and any GitHub App
or installation identifiers with the documented options or environment variables.

For an already configured owner, export the App Configuration endpoint needed to publish the
product index:

```bash
export DSF_OWNER_APPCONFIG_ENDPOINT=https://<owner-appconfig>.azconfig.io
```

Next: [provision a factory](provision-a-factory.md).
