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
  --creation-maturity low   # 'low' routes every PR to a human; 'high' auto-merges on green CI
```

Run `uv run dsf new --help` for the full flag list.

!!! note "Live progress during the Azure step"
    The `provision_azure` step runs `az deployment group create --no-wait` and polls the
    deployment, streaming each Azure resource as it starts and finishes (indented
    `· <type> <name>: <state>` lines) so a multi-minute deployment is never silent. On
    failure the specific failed resource and its reason are surfaced. Tune the poll cadence
    with `DSF_DEPLOY_POLL_INTERVAL` (seconds, default 5).

    The poll is **bounded** by `DSF_DEPLOY_TIMEOUT` (seconds, default 600 = 10 min; set
    `<= 0` to wait indefinitely). If the deployment is still running at the bound, `dsf new`
    cancels it and fails the step naming the still-running resource(s) — rather than hanging.
    A Foundry **Grounding with Bing Search** connection occasionally wedges here on a
    transient Azure 500 while storing its key; re-run `dsf new`, or skip it with
    `dsf new --no-enable-bing-grounding` (the WebIQ agent then runs without web research).

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

## Seed its intent (the charter)

A freshly provisioned factory is **inert**: until a [product charter](operate.md#the-product-charter)
(`.dsf/charter.md`) lands on `main`, the Feature Council has nothing to ground proposals
against and tags every run `uncharted product context`. So `dsf new` doesn't stop at the
infrastructure — on a successful run against a greenfield factory it guides you straight into
seeding intent:

- In an **interactive terminal** it offers to chain into the charter interview:

    ```text
    [dsf] Your factory has no intent yet. Seed its charter now? [Y/n]
    ```

    Answer `Y` and it runs `dsf charter init --product <product>` for you (the interview, then
    a charter PR). Answer `n` and it prints the copy-pasteable next step instead.
- It only prompts for **greenfield** factories — if `.dsf/charter.md` is already on `main` or a
  `charter/*` PR is already open, it says so and doesn't nag.
- It **never blocks** automation: when stdin isn't a TTY, or you pass `--no-charter`, it skips
  the prompt and just prints the next step. Charter seeding is layered on top of provisioning,
  so a failure in the interview never fails `dsf new` itself.

Opening the PR is not the finish line. The charter only becomes authoritative once you
**review and merge** it, after which the next `dsfctl sweep` syncs it into the runtime. The
full path to a *charted* factory is:

```text
dsf new  →  charter PR  →  review & merge  →  dsfctl sweep
```

See [Operate it › The product charter](operate.md#the-product-charter) for the charter
commands in full.

Once a factory exists, move on to [Operate it](operate.md).
