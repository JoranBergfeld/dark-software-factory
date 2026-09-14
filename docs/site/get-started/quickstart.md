# Quickstart

Dark Software Factory (DSF) is the **blueprint**, not a running factory. Install the packaged
CLI once, then stamp out an isolated factory per product with `dsf new`.

For the big picture, read [The loop](../concept/the-loop.md) and
[The harness](../concept/the-harness.md). For design history, see the
[ADRs](https://github.com/JoranBergfeld/dark-software-factory/tree/main/docs/adr).

## Three commands, three jobs

| Command | Scope | Creates or starts | Does not do |
| --- | --- | --- | --- |
| [`dsf bootstrap`](bootstrap.md) | Once per owner; reused across products | Shared GitHub App, App Configuration, and Key Vault | Create a product factory or application |
| [`dsf new --product <product>`](provision-a-factory.md) | One isolated factory per product | Product repo, baseline CI/governance, and Azure factory services | Build application features or deploy the application |
| [`dsf charter implement --product <product>`](implement-application.md) | Application work after charter approval | Constitution PR, then a build issue and agent assignment | Instantly deliver a running application |

**Factory infrastructure is not application infrastructure.** The first two commands
prepare the machinery; the third starts the work that produces the application.

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
  the global tool with `dotnet tool install`; retain its .NET and ASP.NET Core 10 runtimes.
  The self-contained release archives bundle their own runtimes and need no .NET SDK.

Both installation formats include the DSF runtime host and compiled provisioning templates.
No repository checkout, separate `dsf-runtime` installation, or Bicep compiler is required.

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
3. Extract it and put the extracted directory on `PATH`. Keep `runtime/` and `assets/`
   beside `dsf`; do not copy only the executable.
4. Run `dsf --help`.

If DSF reports a missing or corrupt bundled component, reinstall the same package version
or re-extract the **complete** verified archive. Changing directories or cloning this repository
does not repair an incomplete installation. `--config-root` selects instance-state storage,
not a template directory.

## Bootstrap the owner

Before provisioning a product, create the owner App Configuration store, Key Vault, and
DSF GitHub App:

```bash
dsf bootstrap --app-name dsf-<owner> --keyvault-name <owner-keyvault> \
  --appconfig-name <owner-appconfig> --dry-run
```

Review the plan, then rerun without `--dry-run` and export the two endpoints printed
on success. See [Bootstrap](bootstrap.md).

Next: [provision a factory](provision-a-factory.md), then
[implement the application](implement-application.md).
