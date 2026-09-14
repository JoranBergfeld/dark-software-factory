# Bootstrap the owner control plane

`dsf bootstrap` creates the reusable owner App Configuration store, owner Key Vault,
and DSF GitHub App required before provisioning product factories.

**Run once per owner, reuse across products.** This creates neither a product
repository nor your application. Next, [`dsf new`](provision-a-factory.md) creates
a product's factory.

## Prerequisites

- `az login` completed in the target subscription with permission to create resource
  groups, role assignments, App Configuration, and Key Vault resources.
- `gh auth login` completed for the GitHub account or organization that owns product
  repositories.
- Unique, Azure-valid names for the Key Vault and App Configuration store.

## Preview

```bash
dsf bootstrap \
  --app-name dsf-sbx-20260907 \
  --resource-group rg-dsf-app \
  --keyvault-name kvdsfsbx20260907 \
  --appconfig-name appcsdsfsbx20260907 \
  --location swedencentral \
  --dry-run
```

The preview performs no Azure or GitHub mutations.

## Create the owner

Run the same command without `--dry-run`. DSF confirms before creating Azure services
and again before creating the GitHub App. Use `--yes` only for intentional automation.

Bootstrap reports each Azure operation before it starts and confirms resource and
role-assignment completion. App Configuration and Key Vault deployment can take
several minutes. After infrastructure and operator roles are ready, bootstrap
records non-secret stage status at `dsf/owner/bootstrap/<app-name>/status`.
Role-assignment completion does not mean data-plane authorization is already
effective: [Azure allows up to 15 minutes for propagation](https://learn.microsoft.com/en-us/azure/azure-app-configuration/concept-enable-rbac).
Status writes use the App Configuration REST API with the operator's Azure CLI
login, preserving HTTP errors even when Azure returns an empty response body.
On HTTP 403, DSF reports the wait and retries the same status write every 30 seconds,
at most 40 times: a 20-minute retry-wait budget with margin for propagation.
Ctrl+C cancels the wait. Other HTTP errors fail immediately.

If HTTP 403 persists, check **App Configuration Data Owner** for the signed-in
operator and the store's authorized network path (public-access policy or private
endpoint/DNS). Do not disable network restrictions or enable access-key authentication
to get past bootstrap. Resolve access and rerun with the **same resource names**;
creating more stores does not resolve authorization. GitHub App creation has not
started when the initial `Planned` status write fails.

Bootstrap also creates an RBAC-enabled Key Vault with
purge protection, grants the operator required data-plane roles, creates a private
GitHub App with selected-repositories scope, and stores its App ID, installation ID,
and private key in Key Vault. Secrets are never stored in App Configuration.

GitHub App **creation** and **installation** are separate steps:

1. Bootstrap prints a temporary localhost URL serving GitHub's App Manifest form.
   Open it and create the private App. Interactive terminals also attempt to open
   the browser automatically; failure is reported without interrupting bootstrap.
2. GitHub redirects to the local callback. The browser tells you to return to the
   terminal, which acknowledges receipt and exchanges the code for App credentials.
   If localhost is unreachable, paste the redirected URL or code into the terminal
   and press Enter. Input is hidden; do not share callback codes.
3. The terminal prints the App's installation URL. Open it and install the App for
   **selected repositories**. Bootstrap checks every five seconds, for up to five
   minutes, and reports when it finds the installation.
4. Bootstrap stores the credentials in Key Vault, records completion, and prints
   the owner endpoints. Receiving a callback alone does **not** mean setup is complete.

On WSL, open the printed URL in the Windows browser if automatic opening is
unavailable. On a remote host, forward the printed port before opening the URL:

```bash
ssh -L <port>:127.0.0.1:<port> <host>
```

The callback listener remains available for 15 minutes. The automatic callback
and optional pasted input are alternatives: either can complete the step. Pressing
Enter with empty input continues waiting for the browser; Ctrl+C cancels the wait.
Non-interactive sessions wait for the browser callback without prompting.

If a callback must be completed separately, rerun the same command with
`--github-callback '<callback-url-or-code>'`.

After code exchange, credentials are temporarily retained in
`~/.dsf/bootstrap-<app-name>.recovery.json` until Key Vault storage succeeds.
If installation or credential storage fails, finish installing the existing App
and rerun the same bootstrap command; DSF reports recovery and skips App creation.
Do not share or commit the recovery file. If recovery predates installation-link
support, DSF links to GitHub App settings instead; select the existing App and
choose **Install App**.

On success, export the printed endpoints:

```bash
export DSF_OWNER_KEYVAULT_URI=https://<owner-keyvault>.vault.azure.net/
export DSF_OWNER_APPCONFIG_ENDPOINT=https://<owner-appconfig>.azconfig.io
```

Use them for [product provisioning](provision-a-factory.md).
