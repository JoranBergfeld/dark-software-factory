# Provision a factory

The factory CLI is `dsf`. Provisioning a product needs only `--product`:

```bash
uv run dsf new --product <product>
```

Two inputs are inferred so you don't have to pass them:

- **`--owner`** defaults to your gh-authenticated account (resolved via `gh api user`). When
  omitted, DSF prints a warning naming the account; in an interactive terminal it also asks
  you to confirm before creating the repo there. Pass `--owner <org>` to target an
  organization instead.
- **`--name-prefix`** (the base for Azure resource names) defaults to your `--product` key,
  sanitized and randomized to a 12-character, Azure-safe prefix.

**Preview before you commit.** `dsf new` executes for real by default; `--dry-run` prints the
what-if plan and provisions nothing:

```bash
uv run dsf new --product <product> --dry-run                # preview only
uv run dsf new --product <product> --dry-run --write-plan   # preview + persist the manifest
```

The fuller form, pinning everything explicitly:

```bash
uv run dsf new \
  --product microbi \
  --owner my-org \
  --name-prefix microbi \
  --visibility private \
  --location swedencentral \
  --squad-maturity low      # 'low' routes every PR to a human; 'high' auto-merges on green CI
```

Run `uv run dsf new --help` for the full flag list.

!!! note "Live progress during the Azure step"
    The `provision_azure` step runs `az deployment group create --no-wait` and polls the
    deployment, streaming each Azure resource as it starts and finishes (indented
    `· <type> <name>: <state>` lines) so a multi-minute deployment is never silent. On
    failure the specific failed resource and its reason are surfaced. Tune the poll cadence
    with `DSF_DEPLOY_POLL_INTERVAL` (seconds, default 5).

## Prerequisites

Provisioning spans three planes — GitHub, Azure resources, and Azure RBAC — so the principal
running `dsf new` needs:

- **GitHub:** a `gh auth login` session that can create repos under `--owner` and push the
  Coding Squad workflows.
- **Azure subscription RBAC:** **Owner**, or **Contributor + User Access Administrator**, on
  the subscription — the cross-resource-group role assignments (runtime identity, SRE Agent)
  require it.
- **Key Vault reachability for the one-time token seed:** the squad's GitHub token is written
  to the product Key Vault via `az keyvault secret set` as the operator, so the principal is
  granted **Key Vault Secrets Officer** on the vault. Because the vault defaults to
  network-`Deny` (`allowPublicNetworkAccess=false`), run provisioning from a host that can
  reach the vault data plane — deploy with `allowPublicNetworkAccess=true` for a dev instance,
  or provision from inside the vault's network.

## What gets provisioned

A complete, isolated factory for the product:

- a GitHub repo (`<owner>/<product>`) with the DSF label taxonomy and a **Coding Squad**,
- a dedicated Azure resource group (`rg-dsf-<product>`) with the runtime deployed from
  `infra/main.bicep`,
- the product registered in the routing registry (`config/products.json`),
- an **SRE Agent** wired to its production.

The persisted manifest lives under `config/instances/<product>.json`; re-running `dsf new`
for the same product is idempotent (it reuses the persisted name prefix).

Once a factory exists, move on to [Operate it](operate.md).
