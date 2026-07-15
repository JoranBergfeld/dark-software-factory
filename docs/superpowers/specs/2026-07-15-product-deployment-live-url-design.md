# Design: deploy the finished product to a live URL (creation deploy leg)

- Status: Draft
- Date: 2026-07-15
- Scope: `dsf new` provisioning (`cli/src/dsf/instance/`), `infra/main.bicep`, the
  greenfield bootstrap contract (`cli/src/dsf/instance/bootstrap_issue.py`,
  `core/src/dsf/charter/constitution.py`), and the laptop-driven creation watch
  (`cli/src/dsf/cli/charter.py`). Builds on ADR 0004 (Azure Container Apps runtime),
  ADR 0016 (Copilot Coding Agent is the executor; maturity dial governs the merge),
  ADR 0007 (council -> creation handoff), ADR 0017 (constitution is a derived
  projection of the charter), ADR 0014 (real-only `src/`).

## Problem

The Creation phase ends at a **merged pull request**. Nothing deploys the product.
`docs/site/concept/creation.md` states the output is "pull requests against the product
repo, plus product-scoped Lessons"; the Operate/SRE phase only *watches* production, it
does not deploy the app. The `creation_maturity` dial unlocks unattended **merge**
(`high` = auto-merge on green `ci`), never a running product.

So after a charter interview and a high-maturity build, the operator has code on `main`
but no running product and no URL. The intended end state is: **charter interview ->
build -> a live `https://…` URL where the finished product runs.**

Two facts make this non-trivial:

1. **The product stack is arbitrary.** `bootstrap_issue.py` tells the agent
   "a paved-road default is not wired yet — your choice for now." A reliable deploy
   target needs a predictable shape.
2. **No hosting exists.** `infra/main.bicep` provisions only the *factory* runtime
   (the DSF orchestrator Container App) + backing services. There is no container
   registry, no per-product app, and no Azure identity the product repo could deploy
   with.

## Decision

Add a **deploy leg** to Creation, gated on nothing more than a merge to `main`:

- Establish a **container paved road**: every product is a containerized web service
  that serves HTTP on port **8080**. The coding agent produces a real `Dockerfile`.
- `dsf new` provisions **per-product hosting** (registry + Container App + a
  GitHub-OIDC deploy identity) inside the product's own resource group.
- `dsf new` seeds a **`deploy.yml`** GitHub Actions workflow that, on every push to
  `main`, builds the image, rolls it onto the Container App, and records the live URL
  as a GitHub Environment.
- The laptop **watch** (`dsf charter implement` / `dsf charter watch`) follows the
  build past merge and deploy, then **surfaces the URL** (stdout, App Config product
  record, and a comment on the bootstrap issue). A new `dsf charter url` fetches it
  anytime.

Maturity keeps governing **only the merge** (`high` = auto-merge, `low` = a human
approves). Once code lands on `main`, both dials deploy. `dsf offboard` tears the
hosting down by deleting the product resource group, as it does today.

### Why container -> Azure Container Apps

- Works for any web app/API, not just static frontends.
- Reuses the existing ACA investment (ADR 0004) and gives a public FQDN out of the box.
- The product repo needs only a `Dockerfile` — the seeded workflow builds and pushes it
  with the GitHub-hosted runner's Docker, no bespoke per-language pipeline.

Alternatives rejected: a single fixed framework (too constraining for the agent);
agent-authored `azure.yaml` + `azd up` (non-deterministic, agent must also author
infra); Static Web Apps (frontend-only).

## The paved-road contract

The agent must be *told* the target shape, in the two documents that govern its build:

1. **Bootstrap issue** (`bootstrap_issue.py`). Replace step 2's
   "a paved-road default is not wired yet — your choice for now" with an explicit
   contract: the product is a containerized web service; produce a `Dockerfile` at repo
   root that serves HTTP on **port 8080**; a `deploy.yml` workflow builds and deploys it
   on merge to `main`. Keep the "choose a sensible stack" latitude *inside* the
   container.
2. **Constitution** (`core/src/dsf/charter/constitution.py`). Add Core Principle
   **"VI. Deployable Web Service"**: the product ships as a container listening on 8080;
   `Dockerfile` at repo root is a quality gate. Bump `_SCHEMA_VERSION` so
   `is_constitution_current` re-renders the principle into already-provisioned products
   on the next `dsf charter implement`.

No fake application is seeded — the agent writes the real `Dockerfile`. `deploy.yml`
fails loudly if it is missing (a real failure, not a silent stub), consistent with
ADR 0014.

## Infrastructure (`infra/main.bicep`)

All resources are per-product and live in `rg-dsf-{product}`, so the existing
`provision_azure` step deploys them and `dsf offboard` (RG delete) removes them. New
resources:

| Resource | Name | Notes |
|---|---|---|
| `Microsoft.ContainerRegistry/registries` | `{prefix}acr{suffix}` | SKU Basic; admin user off (identity pull) |
| `Microsoft.App/managedEnvironments` | `{prefix}-app-cae-{suffix}` | dedicated product env; log-analytics wired like the factory env |
| `Microsoft.App/containerApps` | `{prefix}-app` | external ingress, `targetPort: 8080`; identity = the deploy UAMI; registry = the ACR via that UAMI; **initial image** `mcr.microsoft.com/azuredocs/containerapps-helloworld:latest` |
| `Microsoft.ManagedIdentity/userAssignedIdentities` | `{prefix}-deploy-{suffix}` | the product-CI (GitHub OIDC) identity |
| `…/federatedIdentityCredentials` (child of the UAMI) | `github-main` | issuer `https://token.actions.githubusercontent.com`, subject `repo:{owner}/{repo}:environment:production`, audience `api://AzureADTokenExchange` |
| `roleAssignments` ×3 | — | UAMI gets **AcrPush** + **AcrPull** on the ACR (scope = registry) and **Contributor scoped to the Container App resource only** |

**Least privilege.** Because the app is pre-created (with the stock hello-world image),
the CI identity only ever runs `az acr build` and `az containerapp update`, so its
Container Apps write is scoped to the single app resource — never the resource group.
The hello-world image is an infra bootstrap seed (the initial ACA revision), replaced on
the first real deploy; the URL returns 503 until then. This was explicitly approved in
place of the looser alternative (workflow creates the app -> RG-wide Contributor).

**Bicep outputs** (consumed by `configure_deploy`): ACR name, managed-env name, app
name, app resource group, deploy-UAMI `clientId`, tenant id, subscription id, and the
app FQDN.

**Ingress/port.** The contract fixes `targetPort: 8080`. The hello-world seed listens on
a different port, so the pre-deploy app is unhealthy by design until the first product
image lands; this is acceptable because no traffic is expected before first deploy.

## Product-repo seeding (`dsf new`)

### Seeded workflow — `.github/workflows/deploy.yml`

Written during the existing `seed_repo` step (committed with the `.specify` scaffold and
baseline `ci.yml`). The file is **product-agnostic**: it reads all coordinates from repo
variables, so no rendering is needed.

```yaml
name: deploy
on:
  push:
    branches: [main]
permissions:
  id-token: write
  contents: read
  deployments: write
concurrency: deploy-${{ github.ref }}
jobs:
  deploy:
    runs-on: ubuntu-latest
    environment:
      name: production
      url: ${{ steps.url.outputs.fqdn }}
    steps:
      - uses: actions/checkout@v4
      - uses: azure/login@v2
        with:
          client-id: ${{ vars.AZURE_CLIENT_ID }}
          tenant-id: ${{ vars.AZURE_TENANT_ID }}
          subscription-id: ${{ vars.AZURE_SUBSCRIPTION_ID }}
      - name: Log in to the registry
        run: az acr login --name ${{ vars.DSF_ACR_NAME }}   # uses AcrPush
      - name: Build + push the image
        run: |
          IMAGE=${{ vars.DSF_ACR_NAME }}.azurecr.io/${{ vars.DSF_ACA_APP }}:${{ github.sha }}
          docker build -t "$IMAGE" .
          docker push "$IMAGE"
      - name: Roll onto Container Apps
        run: az containerapp update -g ${{ vars.DSF_ACA_RG }} -n ${{ vars.DSF_ACA_APP }}
             --image ${{ vars.DSF_ACR_NAME }}.azurecr.io/${{ vars.DSF_ACA_APP }}:${{ github.sha }}
      - name: Read URL
        id: url
        run: echo "fqdn=https://$(az containerapp show -g ${{ vars.DSF_ACA_RG }}
             -n ${{ vars.DSF_ACA_APP }} --query properties.configuration.ingress.fqdn -o tsv)"
             >> "$GITHUB_OUTPUT"
      - run: echo "Deployed ${{ steps.url.outputs.fqdn }}" >> "$GITHUB_STEP_SUMMARY"
```

`environment: production` both drives the repo's Environments-tab URL and matches the
federated-credential subject, so OIDC login only works from that environment.

### New provision step — `configure_deploy`

Runs after `provision_azure` and `publish_runtime_index` (it needs the bicep outputs),
adjacent to `branch_protection`. Idempotent `gh` calls with the operator's interactive
auth (same pattern as `branch_protection`):

- Create the GitHub `production` **Environment** (`gh api --method PUT
  repos/{repo}/environments/production`).
- Set repo **variables** (`gh variable set`): `AZURE_CLIENT_ID`, `AZURE_TENANT_ID`,
  `AZURE_SUBSCRIPTION_ID`, `DSF_ACR_NAME`, `DSF_ACA_ENV`, `DSF_ACA_APP`, `DSF_ACA_RG`.

No secrets are stored on the repo — deploy auth is OIDC only.

### Workflow-tampering hardening

The coding agent has `contents: write` and could edit `deploy.yml`; under high-maturity
auto-merge that edit would self-merge. Mitigation seeded by `dsf new`: a `CODEOWNERS`
file assigning `.github/workflows/*` to the product repo owner (`spec.owner`), plus
flipping `require_code_owner_review` to `True` in `branch_protection.ruleset_payload`.
CODEOWNERS review is only demanded when a PR touches a matching path, so ordinary
product PRs are unaffected while any PR editing a workflow requires the owner's approval
— even under high maturity's `required_approving_review_count: 0`. This keeps the
privileged deploy path off the auto-merge road.

## CLI — watch, URL surfacing, teardown

### Extend the watch

`dsf charter implement` and `dsf charter watch` call `_watch_and_request_review`, which
today returns once Copilot review is requested. Extend it with a **deploy phase** after
hand-off (same default 30-min bound; resumable `rc=2`):

1. Wait until the agent PR is **merged** (`high` auto-merges; `low` waits for a human —
   a long wait simply times out to `rc=2`, resumed later by `dsf charter watch`).
2. Wait for the **`deploy` workflow run** on the merge commit to conclude `success`.
3. Read the app FQDN (`az containerapp show`).
4. Surface it: print `[dsf] live: https://<fqdn>`, write it to the App Config **product
   record** (`deploy_url`), and comment the URL on the bootstrap issue.

The deploy phase is a distinct, separately testable helper so the existing
review-handoff logic stays intact.

### New `dsf charter url --product X`

Prints the live FQDN, read from `az containerapp show` (truthful, never stale); falls
back to the App Config product record if the app is not yet reachable. Fetchable at any
time, independent of the watch.

### Teardown

No new offboard logic: ACR, managed env, app, UAMI, and federated credential all live in
`rg-dsf-{product}`, which `dsf offboard` already deletes. Add only a best-effort delete
of the GitHub `production` Environment during offboard for cleanliness.

## Testing

Follow existing conventions (`uv` only; `dsf_testing` recording doubles; per-member
`tests/`):

- **Provisioner** (`cli/tests/instance/test_provisioner.py`): the plan lists
  `configure_deploy`; dry-run stays pure (no `gh`/`az` side effects); the
  `gh variable set` / environment-create command builders are correct; bicep additions
  reflected where the tests assert on the deployment.
- **Deploy workflow + config**: unit-test the static `deploy.yml` contents and the
  repo-variable/environment command builders.
- **Contract docs**: `bootstrap_issue.py` renders the container/8080 contract;
  `render_constitution` emits Principle VI and the bumped `_SCHEMA_VERSION`;
  `is_constitution_current` returns `False` for a pre-bump constitution.
- **Branch protection** (`cli/tests/instance/test_branch_protection.py`): `ruleset_payload`
  now sets `require_code_owner_review: True`; `seed_repo` writes the `CODEOWNERS` guarding
  `.github/workflows/*`.
- **Watch** (`cli/tests/cli/test_charter.py`): the deploy phase advances
  merged -> workflow-success -> FQDN and surfaces it; times out to `rc=2` when unmerged;
  `dsf charter url` reads live then falls back to App Config. Drive `gh`/`az` through the
  injected runner seam.

Gate (verified commands): `uv run pytest -q`, `uv run ruff check .`,
`uv run lint-imports` (expect "kept, 0 broken").

## Risks and tradeoffs

1. **Dedicated env per product** adds ~2-4 min to provisioning and consumes the region's
   managed-environment quota (~15 by default). Reusing the factory env is a one-line
   switch if the quota bites; kept dedicated for blast-radius isolation per the chosen
   topology.
2. **Workflow tampering** under auto-merge — mitigated by the CODEOWNERS +
   code-owner-review rule on `.github/workflows/**` above.
3. **Low-maturity latency**: a human may take days to merge; the watch times out to
   `rc=2` and resumes. Expected, not a failure.
4. **Bootstrap image 503s** until the first real deploy. Expected.
5. **Tenant Azure Policy** (this environment) forces some resources private; ACA external
   ingress is a public FQDN and is unaffected. ACR Basic public endpoint is used; if a
   policy forces registry-private later, `az acr build` from GitHub-hosted runners would
   need a self-hosted/VNet runner — out of scope here.

## Out of scope

- Custom domains / TLS beyond the default `*.azurecontainerapps.io` FQDN.
- Multiple environments (staging/preview) — only `production` on `main`.
- Rollback/blue-green beyond ACA's single-revision replace.
- Non-web products (jobs, batch) — the paved road is a web service.
