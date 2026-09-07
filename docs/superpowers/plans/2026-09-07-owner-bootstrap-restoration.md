# Owner Bootstrap Restoration Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace the `dsf bootstrap` no-op with a durable owner control-plane setup and wire its GitHub App credentials into product provisioning and charter operations.

**Architecture:** Keep `CliApplication` as the command composition boundary. Add focused `Dsf.Cli` owner-bootstrap components for Azure owner resources, non-secret App Configuration progress, GitHub App Manifest conversion, and Key Vault credential storage. Reuse the existing injectable Azure CLI seam; retain secret values only inside narrow credential objects and Key Vault operations.

**Tech Stack:** .NET 10, System.CommandLine, Azure CLI, GitHub REST API/App Manifest flow, Azure App Configuration, Azure Key Vault, xUnit.

---

## File structure

| File | Responsibility |
|---|---|
| `dotnet/src/Dsf.Core/Runtime/ProductConfigurationKeys.cs` | Canonical owner-bootstrap status key builder; no CLI-specific I/O. |
| `dotnet/src/Dsf.Cli/OwnerBootstrap.cs` | Requests/results, status state model, orchestration, and interfaces. |
| `dotnet/src/Dsf.Cli/AzureCliOwnerBootstrapClient.cs` | `az` commands for owner resource/RBAC provisioning, App Configuration status writes, and Key Vault credential read/write/copy. |
| `dotnet/src/Dsf.Cli/GitHubAppBootstrapClient.cs` | Least-privilege manifest, callback/paste-code capture, conversion, signed installation discovery. |
| `dotnet/src/Dsf.Cli/OwnerCredentialResolver.cs` | Resolve persisted owner App identity for `new` and charter paths. |
| `infra/owner-keyvault.bicep` | Hardened, parameterized owner Key Vault definition used only by bootstrap. |
| `dotnet/src/Dsf.Cli/CliApplication.cs` | Add `bootstrap --dry-run|--yes`, confirmation gates, and compose the new module. |
| `dotnet/src/Dsf.Cli/CharterRepositoryClient.cs` | Build GitHub App installation-token auth from owner credentials when a user token is absent. |
| `dotnet/src/Dsf.Cli/PlannedInstanceDefinition.cs` | Fold resolved owner App identifiers into the clean product definition. |
| `dotnet/src/Dsf.Cli/AzureProvisioningPlan.cs` | Add the product-vault private-key copy request after topology deployment. |
| `dotnet/src/Dsf.Cli/AzureCliProvisioningClient.cs` | Execute product private-key copy without logging PEM content. |
| `dotnet/tests/Dsf.Cli.Tests/OwnerBootstrapTests.cs` | Unit tests for the owner-bootstrap state machine and credentials. |
| `dotnet/tests/Dsf.Cli.Tests/GitHubAppBootstrapClientTests.cs` | Manifest, code parsing, conversion, signed installation-discovery tests. |
| `dotnet/tests/Dsf.Cli.Tests/OwnerBootstrapAzureClientTests.cs` | Exact owner Azure CLI command shapes, retries, redaction, reconciliation tests. |
| Existing CLI/provisioning/charter test files | Command grammar, integration wiring, credential propagation, and authentication regression coverage. |
| `README.md`, `docs/site/get-started/{bootstrap,quickstart,provision-a-factory,operate}.md`, `.env.example` | Living owner-bootstrap and end-to-end operator documentation. |

### Task 1: Define owner-bootstrap contracts and status keys

**Files:**
- Modify: `dotnet/src/Dsf.Core/Runtime/ProductConfigurationKeys.cs`
- Create: `dotnet/src/Dsf.Cli/OwnerBootstrap.cs`
- Create: `dotnet/tests/Dsf.Cli.Tests/OwnerBootstrapTests.cs`
- Modify: `dotnet/tests/Dsf.Cli.Tests/AppConfigurationClientTests.cs`

- [ ] **Step 1: Write failing status-key and model tests**

```csharp
[Fact]
public void Owner_bootstrap_status_key_is_stable_per_app()
{
    Assert.Equal(
        "dsf/owner/bootstrap/dsf-sbx-20260907/status",
        ProductConfigurationKeys.OwnerBootstrapStatus("dsf-sbx-20260907"));
}

[Fact]
public async Task Bootstrap_records_non_secret_boundaries_in_order()
{
    var status = new RecordingOwnerBootstrapStatusStore();
    var sut = new OwnerBootstrapper(new RecordingOwnerInfrastructure(), status, new RecordingGitHubAppBootstrapper(), new RecordingOwnerCredentialStore());

    await sut.ExecuteAsync(SampleRequest(), BootstrapConfirmation.AllApproved, CancellationToken.None);

    Assert.Equal(
        [OwnerBootstrapStage.AppConfigurationReady, OwnerBootstrapStage.KeyVaultReady,
         OwnerBootstrapStage.GitHubAppCreated, OwnerBootstrapStage.CredentialsStored,
         OwnerBootstrapStage.Completed],
        status.Updates.Select(update => update.Stage));
    Assert.DoesNotContain(status.SerializedValues, value => value.Contains("BEGIN", StringComparison.Ordinal));
}
```

- [ ] **Step 2: Run the new tests to verify failure**

Run:

```bash
cd dotnet && dotnet test tests/Dsf.Cli.Tests/Dsf.Cli.Tests.csproj --filter "FullyQualifiedName~OwnerBootstrapTests|FullyQualifiedName~AppConfigurationClientTests" --no-build
```

Expected: FAIL because `OwnerBootstrapper`, `OwnerBootstrapStage`, and `OwnerBootstrapStatus` do not exist.

- [ ] **Step 3: Add the minimal canonical key and domain contracts**

Add this public key builder in `ProductConfigurationKeys`:

```csharp
public static string OwnerBootstrapStatus(string appName) =>
    $"dsf/owner/bootstrap/{(appName ?? string.Empty).Trim().ToLowerInvariant()}/status";
```

Create `OwnerBootstrap.cs` with:

```csharp
internal enum OwnerBootstrapStage
{
    Planned,
    AppConfigurationReady,
    KeyVaultReady,
    GitHubAppCreated,
    CredentialsStored,
    Completed,
    Failed,
}

internal sealed record OwnerBootstrapRequest(
    string AppName,
    string ResourceGroup,
    string KeyVaultName,
    string AppConfigName,
    string Location,
    string SubscriptionId,
    string OperatorObjectId);

internal sealed record OwnerBootstrapStatus(
    OwnerBootstrapStage Stage,
    DateTimeOffset UpdatedAt,
    IReadOnlyList<OwnerBootstrapStage> CompletedStages,
    string? KeyVaultUri,
    string? AppConfigEndpoint,
    string? AppId,
    string? InstallationId,
    string? Error);
```

Define narrow interfaces for infrastructure, status writes, GitHub creation, and credential storage. Serialize only the `OwnerBootstrapStatus` record to the status key; reject values containing PEM markers before a status write.

- [ ] **Step 4: Implement orchestrator ordering and failed-status writes**

Implement `OwnerBootstrapper.ExecuteAsync` as a small state machine:

```csharp
await statusStore.WriteAsync(request, status with { Stage = OwnerBootstrapStage.Planned }, cancellationToken);
var authority = await infrastructure.EnsureAsync(request, cancellationToken);
await statusStore.WriteAsync(request, status with { Stage = OwnerBootstrapStage.AppConfigurationReady, AppConfigEndpoint = authority.AppConfigEndpoint }, cancellationToken);
await statusStore.WriteAsync(request, status with { Stage = OwnerBootstrapStage.KeyVaultReady, KeyVaultUri = authority.KeyVaultUri }, cancellationToken);
var credentials = await appBootstrapper.GetOrCreateAsync(request, cancellationToken);
await credentialStore.WriteAsync(authority.KeyVaultUri, credentials, cancellationToken);
```

After each successful boundary, append the stage to `CompletedStages`. Wrap only the orchestration boundary so exceptions first write a sanitized `Failed` status and then rethrow; cancellation stays cancellation. The sanitizer must retain the exception type/message only after removing PEM blocks and never include command output.

- [ ] **Step 5: Run focused tests**

Run:

```bash
cd dotnet && dotnet test tests/Dsf.Cli.Tests/Dsf.Cli.Tests.csproj --filter "FullyQualifiedName~OwnerBootstrapTests|FullyQualifiedName~AppConfigurationClientTests" --no-build
```

Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add dotnet/src/Dsf.Core/Runtime/ProductConfigurationKeys.cs dotnet/src/Dsf.Cli/OwnerBootstrap.cs dotnet/tests/Dsf.Cli.Tests/OwnerBootstrapTests.cs dotnet/tests/Dsf.Cli.Tests/AppConfigurationClientTests.cs
git commit -m "feat: add owner bootstrap contracts"
```

### Task 2: Implement idempotent Azure owner authority and remote persistence

**Files:**
- Create: `infra/owner-keyvault.bicep`
- Create: `dotnet/src/Dsf.Cli/AzureCliOwnerBootstrapClient.cs`
- Create: `dotnet/tests/Dsf.Cli.Tests/OwnerBootstrapAzureClientTests.cs`
- Modify: `dotnet/src/Dsf.Cli/OwnerBootstrap.cs`

- [ ] **Step 1: Write failing Azure command and retry tests**

```csharp
[Fact]
public async Task Ensure_creates_owner_authority_before_github_work()
{
    var runner = new RecordingAzureCliRunner(
        Success("sub-id"), Success("operator-id"), Success(), Success(), Success(), Success());
    var client = new AzureCliOwnerBootstrapClient(runner, Delay.NoWait);

    await client.EnsureAsync(SampleRequest(), CancellationToken.None);

    Assert.Equal(["account", "show", "--query", "id", "-o", "tsv"], runner.Invocations[0]);
    Assert.Equal(["ad", "signed-in-user", "show", "--query", "id", "-o", "tsv"], runner.Invocations[1]);
    Assert.Contains(["appconfig", "create", "--name", "appcsdsfsbx20260907", "--resource-group", "rg-dsf-app",
        "--location", "swedencentral", "--sku", "Standard", "--disable-local-auth", "true"], runner.Invocations);
}

[Fact]
public async Task Write_credentials_retries_role_propagation_without_echoing_pem()
{
    var runner = new RecordingAzureCliRunner(Failure("Forbidden"), Success(), Success(), Success());
    var client = new AzureCliOwnerBootstrapClient(runner, new RecordingDelay());

    await client.WriteAsync("https://kv.vault.azure.net/", SampleCredentials(), CancellationToken.None);

    Assert.DoesNotContain(runner.Invocations.SelectMany(x => x), arg => arg.Contains("BEGIN RSA", StringComparison.Ordinal));
    Assert.All(runner.Invocations.Where(x => x.Contains("secret")), command => Assert.Contains("-o", command));
}
```

- [ ] **Step 2: Run the focused tests to verify failure**

Run:

```bash
cd dotnet && dotnet test tests/Dsf.Cli.Tests/Dsf.Cli.Tests.csproj --filter FullyQualifiedName~OwnerBootstrapAzureClientTests --no-build
```

Expected: FAIL because `AzureCliOwnerBootstrapClient` does not exist.

- [ ] **Step 3: Implement owner resource reconciliation**

Implement `AzureCliOwnerBootstrapClient` against existing `IAzureCliRunner`, never a separate process seam:

```csharp
["group", "create", "--name", request.ResourceGroup, "--location", request.Location]
["appconfig", "create", "--name", request.AppConfigName, "--resource-group", request.ResourceGroup,
 "--location", request.Location, "--sku", "Standard", "--disable-local-auth", "true"]
["deployment", "group", "create", "--resource-group", request.ResourceGroup,
 "--name", $"dsf-owner-kv-{request.KeyVaultName}",
 "--template-file", Path.Combine(repoRoot, "infra", "owner-keyvault.bicep"),
 "--parameters", $"vaultName={request.KeyVaultName}", $"location={request.Location}"]
```

Grant the signed-in user `App Configuration Data Owner` at the configuration store scope and `Key Vault Secrets Officer` at the vault scope using `az role assignment create`. Treat Azure's idempotent create responses as success; do not suppress unrelated non-zero errors. Return normalized `https://<name>.azconfig.io` and `https://<name>.vault.azure.net/` endpoints.

Create `infra/owner-keyvault.bicep` using the repository Bicep conventions: documented
parameters at the top, lower-camel resource symbol, and an explicit
`Microsoft.KeyVault/vaults@2024-11-01` resource with `sku.standard`,
`enableRbacAuthorization: true`, `enablePurgeProtection: true`,
`softDeleteRetentionInDays: 90`, and no access policies. Do not output secret
values.

- [ ] **Step 4: Implement status and secret operations**

Write the status document with:

```csharp
["appconfig", "kv", "set", "--endpoint", endpoint, "--auth-mode", "login",
 "--key", ProductConfigurationKeys.OwnerBootstrapStatus(appName),
 "--value", JsonSerializer.Serialize(status), "--yes"]
```

Read the exact three owner secret names with `az keyvault secret show ... --query value -o tsv`; return `Complete`, `Empty`, or `Partial(missingNames)`. A partial state throws a clear `InvalidOperationException`. Write ID and installation values with `--value` and PEM with a mode-0600 temporary file plus `--file`; use `-o none` on every `secret set`, retrying only the initial authorization propagation failures a bounded eight times at 15 seconds.

- [ ] **Step 5: Add exact redaction/reconciliation tests and run them**

Add tests for:

- existing complete credentials bypassing GitHub creation;
- partial credentials reporting exactly the missing names;
- `-o none` on every secret write;
- temporary PEM deletion in `finally`;
- status serialization excluding private-key text;
- role-assignment and secret-write failures preserving stderr in the thrown error.

Run:

```bash
cd dotnet && dotnet test tests/Dsf.Cli.Tests/Dsf.Cli.Tests.csproj --filter "FullyQualifiedName~OwnerBootstrapAzureClientTests|FullyQualifiedName~OwnerBootstrapTests" --no-build
```

Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add infra/owner-keyvault.bicep dotnet/src/Dsf.Cli/AzureCliOwnerBootstrapClient.cs dotnet/src/Dsf.Cli/OwnerBootstrap.cs dotnet/tests/Dsf.Cli.Tests/OwnerBootstrapAzureClientTests.cs dotnet/tests/Dsf.Cli.Tests/OwnerBootstrapTests.cs
git commit -m "feat: provision owner bootstrap authority"
```

### Task 3: Implement GitHub App Manifest creation and safe recovery

**Files:**
- Create: `dotnet/src/Dsf.Cli/GitHubAppBootstrapClient.cs`
- Create: `dotnet/tests/Dsf.Cli.Tests/GitHubAppBootstrapClientTests.cs`
- Modify: `dotnet/src/Dsf.Cli/OwnerBootstrap.cs`

- [ ] **Step 1: Write failing manifest and callback parsing tests**

```csharp
[Fact]
public void Manifest_has_only_required_permissions_and_no_webhook()
{
    var manifest = GitHubAppManifest.Create("dsf-sbx-20260907", CallbackUri);

    Assert.Equal("write", manifest.DefaultPermissions["issues"]);
    Assert.Equal("write", manifest.DefaultPermissions["pull_requests"]);
    Assert.Equal("write", manifest.DefaultPermissions["contents"]);
    Assert.Equal("write", manifest.DefaultPermissions["administration"]);
    Assert.Empty(manifest.DefaultEvents);
    Assert.False(manifest.Public);
}

[Theory]
[InlineData("abc123", "abc123")]
[InlineData("?code=abc123", "abc123")]
[InlineData("http://127.0.0.1:8765/callback?code=abc123&state=x", "abc123")]
public void Callback_code_parser_accepts_headless_forms(string raw, string expected) =>
    Assert.Equal(expected, GitHubAppManifest.ParseCode(raw));
```

- [ ] **Step 2: Run the tests to verify failure**

Run:

```bash
cd dotnet && dotnet test tests/Dsf.Cli.Tests/Dsf.Cli.Tests.csproj --filter FullyQualifiedName~GitHubAppBootstrapClientTests --no-build
```

Expected: FAIL because `GitHubAppManifest` and `GitHubAppBootstrapClient` do not exist.

- [ ] **Step 3: Implement manifest conversion and installation discovery**

Create a `GitHubAppBootstrapClient` using injected `HttpClient`, clock, delay, local-callback opener, and code prompt. Manifest JSON must be:

```json
{
  "name": "<app-name>",
  "url": "http://127.0.0.1:8765/callback",
  "redirect_url": "http://127.0.0.1:8765/callback",
  "public": false,
  "default_permissions": {
    "issues": "write",
    "pull_requests": "write",
    "contents": "write",
    "administration": "write"
  }
}
```

Render a mode-0600 temporary HTML form posting to
`https://github.com/settings/apps/new`, launch it with the platform browser opener,
and host one `HttpListener` callback for 120 seconds. If no callback arrives, print
the local form path and accept a bare code, query fragment, or full callback URL from
the terminal. Delete temporary HTML in `finally`.

POST the code to `/app-manifests/{code}/conversions`. Import the PEM with
`RSA.ImportFromPem`, create a nine-minute RS256 JWT (`iat` backdated 60 seconds,
`exp`, `iss` App ID), and poll `/app/installations` up to 60 times every five seconds
until exactly one selected-repositories installation is found. Fail loudly if none or
more than one installation is returned.

- [ ] **Step 4: Implement and test durable recovery**

Before Key Vault accepts converted credentials, write a mode-0600 recovery file under
`~/.dsf/bootstrap-<app-name>.recovery.json`; it contains App ID, installation ID, and
PEM only. On re-run, use it only after owner Azure authority reconciliation and skip
manifest conversion. Remove it in `finally` after the credential-store success signal.

Add tests for conversion request shape, JWT claims, timeout-to-paste fallback,
installation polling, malformed callback rejection, ambiguous installation failure,
recovery reuse, and recovery deletion.

- [ ] **Step 5: Run focused tests**

Run:

```bash
cd dotnet && dotnet test tests/Dsf.Cli.Tests/Dsf.Cli.Tests.csproj --filter "FullyQualifiedName~GitHubAppBootstrapClientTests|FullyQualifiedName~OwnerBootstrapTests" --no-build
```

Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add dotnet/src/Dsf.Cli/GitHubAppBootstrapClient.cs dotnet/src/Dsf.Cli/OwnerBootstrap.cs dotnet/tests/Dsf.Cli.Tests/GitHubAppBootstrapClientTests.cs dotnet/tests/Dsf.Cli.Tests/OwnerBootstrapTests.cs
git commit -m "feat: create owner GitHub App"
```

### Task 4: Wire the real `dsf bootstrap` command and safe interaction

**Files:**
- Modify: `dotnet/src/Dsf.Cli/CliApplication.cs`
- Modify: `dotnet/tests/Dsf.Cli.Tests/CliCommandSurfaceTests.cs`
- Modify: `dotnet/tests/Dsf.Cli.Tests/CliInteractionTests.cs`
- Modify: `dotnet/tests/Dsf.Cli.Tests/OwnerBootstrapTests.cs`

- [ ] **Step 1: Write failing CLI grammar and interaction tests**

```csharp
[Fact]
public void Bootstrap_surface_includes_safety_options()
{
    var bootstrap = CliApplication.BuildRootCommand().Subcommands.Single(command => command.Name == "bootstrap");
    Assert.Contains(bootstrap.Options, option => option.Name == "--dry-run");
    Assert.Contains(bootstrap.Options, option => option.Name == "--yes");
}

[Fact]
public async Task Bootstrap_dry_run_prints_plan_without_calling_azure_or_github()
{
    var bootstrap = new RecordingOwnerBootstrapper();
    var result = await InvokeBootstrapAsync(["bootstrap", RequiredArgs(), "--dry-run"], PlainTerminal(), bootstrap);

    Assert.Equal(0, result.ExitCode);
    Assert.Contains("owner App Configuration", result.Terminal.Output, StringComparison.OrdinalIgnoreCase);
    Assert.Empty(bootstrap.Requests);
}
```

- [ ] **Step 2: Run the CLI tests to verify failure**

Run:

```bash
cd dotnet && dotnet test tests/Dsf.Cli.Tests/Dsf.Cli.Tests.csproj --filter "FullyQualifiedName~CliCommandSurfaceTests|FullyQualifiedName~CliInteractionTests|FullyQualifiedName~OwnerBootstrapTests" --no-build
```

Expected: FAIL because bootstrap lacks the safety options and remains a no-op.

- [ ] **Step 3: Compose the bootstrap module without widening production globals**

Add `--dry-run` and `--yes` to `BuildBootstrapCommand`. Add an internal
`CliDependencies` record (or an equivalent single composition object) so production
construction uses concrete owner-bootstrap clients while tests inject recorders,
without continually multiplying `InvokeAsync` overloads.

For `--dry-run`, validate names and render all stages but do not construct browser
listeners or call Azure/GitHub. For live runs, prompt twice unless `--yes`:

```text
[dsf] This will create owner Azure services in rg-dsf-app. Continue? [y/N]
[dsf] This will create a private GitHub App and installation. Continue? [y/N]
```

Refusing either confirmation exits non-zero and writes a remote canceled status only
when App Configuration already exists. Non-interactive live invocation without
`--yes` fails loudly and suggests `--dry-run` or `--yes`.

- [ ] **Step 4: Update the frozen surface snapshot and run tests**

Update `ExpectedSnapshot` with the two new bootstrap options in declaration order.
Add coverage for cancellation propagation and sanitized terminal errors.

Run:

```bash
cd dotnet && dotnet test tests/Dsf.Cli.Tests/Dsf.Cli.Tests.csproj --filter "FullyQualifiedName~CliCommandSurfaceTests|FullyQualifiedName~CliInteractionTests|FullyQualifiedName~OwnerBootstrapTests" --no-build
```

Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add dotnet/src/Dsf.Cli/CliApplication.cs dotnet/tests/Dsf.Cli.Tests/CliCommandSurfaceTests.cs dotnet/tests/Dsf.Cli.Tests/CliInteractionTests.cs dotnet/tests/Dsf.Cli.Tests/OwnerBootstrapTests.cs
git commit -m "feat: run durable owner bootstrap"
```

### Task 5: Consume owner credentials in `dsf new` and deployed product runtime

**Files:**
- Create: `dotnet/src/Dsf.Cli/OwnerCredentialResolver.cs`
- Modify: `dotnet/src/Dsf.Cli/CliApplication.cs`
- Modify: `dotnet/src/Dsf.Cli/PlannedInstanceDefinition.cs`
- Modify: `dotnet/src/Dsf.Cli/AzureProvisioningPlan.cs`
- Modify: `dotnet/src/Dsf.Cli/AzureCliProvisioningClient.cs`
- Modify: `dotnet/tests/Dsf.Cli.Tests/NewInstanceDefinitionTests.cs`
- Modify: `dotnet/tests/Dsf.Cli.Tests/AzureProvisioningPlaneTests.cs`
- Modify: `dotnet/tests/Dsf.Cli.Tests/AzureCliProvisioningClientTests.cs`

- [ ] **Step 1: Write failing automatic-resolution and key-copy tests**

```csharp
[Fact]
public async Task New_resolves_owner_app_identifiers_when_not_explicitly_provided()
{
    var resolver = new RecordingOwnerCredentialResolver(new OwnerGitHubIdentity("7", "42"));

    var definition = await InvokeNewAndReadDefinitionAsync(resolver);

    Assert.Equal("7", definition.GitHub.AppId);
    Assert.Equal("42", definition.GitHub.InstallationId);
}

[Fact]
public async Task Product_provisioning_copies_owner_pem_to_product_vault_without_exposing_value()
{
    var azure = new RecordingAzureProvisioningClient();
    await AzureProvisioningPlan.Build(SampleDefinition(), "/repo-root").ExecuteAsync(azure, CancellationToken.None);

    Assert.IsType<CopyOwnerAppPrivateKeyRequest>(azure.Requests[2]);
    Assert.DoesNotContain("BEGIN", InstanceDefinitions.Serialize(SampleDefinition()), StringComparison.Ordinal);
}
```

- [ ] **Step 2: Run the new tests to verify failure**

Run:

```bash
cd dotnet && dotnet test tests/Dsf.Cli.Tests/Dsf.Cli.Tests.csproj --filter "FullyQualifiedName~NewInstanceDefinitionTests|FullyQualifiedName~AzureProvisioningPlaneTests|FullyQualifiedName~AzureCliProvisioningClientTests" --no-build
```

Expected: FAIL because no owner resolver or product-vault copy request exists.

- [ ] **Step 3: Resolve owner identity before GitHub/Azure product plans**

Implement:

```csharp
internal sealed record OwnerGitHubIdentity(string AppId, string InstallationId);
internal interface IOwnerCredentialResolver
{
    Task<OwnerGitHubIdentity> ResolveIdentityAsync(string ownerKeyVaultUri, CancellationToken cancellationToken);
}
```

When both CLI/env identifiers are absent and `DSF_OWNER_KEYVAULT_URI` is set, resolve
the two non-secret identifiers from Key Vault before `PlannedInstanceDefinition.Build`.
Explicit CLI options remain highest precedence. If only one explicit identifier is
given, fail rather than mix identities. Require the owner vault for a live product
run; dry run may render absent credentials only when it would not claim a functional
runtime.

- [ ] **Step 4: Copy the PEM after product topology returns its Key Vault URI**

Add `CopyOwnerAppPrivateKeyRequest` after `DeployTopologyRequest` and before the SRE
request. Its execution reads `github-app-private-key` from the owner vault using
Azure CLI login, writes it to a mode-0600 temporary file, then runs:

```text
az keyvault secret set --vault-name <product-vault-name> \
  --name github-app-private-key --file <private-temp-path> -o none
```

Delete the temporary file in `finally`. The request carries vault URIs and secret
names, never PEM data; Azure request/result logging and instance manifests remain
secret-free. Fail loudly if topology has no `keyVaultUri` or `keyVaultName`.

- [ ] **Step 5: Run product provisioning tests**

Run:

```bash
cd dotnet && dotnet test tests/Dsf.Cli.Tests/Dsf.Cli.Tests.csproj --filter "FullyQualifiedName~NewInstanceDefinitionTests|FullyQualifiedName~AzureProvisioningPlaneTests|FullyQualifiedName~AzureCliProvisioningClientTests" --no-build
```

Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add dotnet/src/Dsf.Cli/OwnerCredentialResolver.cs dotnet/src/Dsf.Cli/CliApplication.cs dotnet/src/Dsf.Cli/PlannedInstanceDefinition.cs dotnet/src/Dsf.Cli/AzureProvisioningPlan.cs dotnet/src/Dsf.Cli/AzureCliProvisioningClient.cs dotnet/tests/Dsf.Cli.Tests/NewInstanceDefinitionTests.cs dotnet/tests/Dsf.Cli.Tests/AzureProvisioningPlaneTests.cs dotnet/tests/Dsf.Cli.Tests/AzureCliProvisioningClientTests.cs
git commit -m "feat: wire owner credentials into product provisioning"
```

### Task 6: Make charter operations authenticate through owner credentials

**Files:**
- Modify: `dotnet/src/Dsf.Cli/CharterRepositoryClient.cs`
- Modify: `dotnet/src/Dsf.Cli/CliApplication.cs`
- Modify: `dotnet/tests/Dsf.Cli.Tests/GitHubCharterRepositoryClientTests.cs`
- Modify: `dotnet/tests/Dsf.Cli.Tests/CharterCommandTests.cs`

- [ ] **Step 1: Write failing owner-App charter authentication tests**

```csharp
[Fact]
public async Task Charter_client_uses_installation_bearer_token_when_owner_identity_is_available()
{
    var handler = new RecordingHttpMessageHandler(InstallationTokenResponse(), CharterContentsResponse());
    var client = new GitHubCharterRepositoryClient(new HttpClient(handler), token: null, ownerCredentials: SampleOwnerCredentials());

    await client.ReadAsync("acme/demo", ".dsf/charter.md", "main", CancellationToken.None);

    Assert.Equal("Bearer installation-token", handler.Requests[1].Headers.Authorization!.ToString());
}
```

- [ ] **Step 2: Run charter tests to verify failure**

Run:

```bash
cd dotnet && dotnet test tests/Dsf.Cli.Tests/Dsf.Cli.Tests.csproj --filter "FullyQualifiedName~GitHubCharterRepositoryClientTests|FullyQualifiedName~CharterCommandTests" --no-build
```

Expected: FAIL because `GitHubCharterRepositoryClient` supports only `GH_TOKEN`/`GITHUB_TOKEN`.

- [ ] **Step 3: Add App-token authentication fallback**

Refactor the charter HTTP client behind an internal token provider. Preserve the
existing user-token behavior when `GH_TOKEN`/`GITHUB_TOKEN` is present. Otherwise,
resolve the owner App ID, installation ID, and PEM from `DSF_OWNER_KEYVAULT_URI`,
create the same short-lived RS256 App JWT used by bootstrap, call:

```text
POST /app/installations/<installation-id>/access_tokens
Authorization: Bearer <app-jwt>
Accept: application/vnd.github+json
```

Cache the installation token only until its returned expiry. The PEM never appears
in exception messages, recorded requests, or test diagnostics. Retain the existing
explicit failure when neither a user token nor owner configuration is available.

- [ ] **Step 4: Test fallback, precedence, expiry, and missing configuration**

Add tests proving user-token precedence, successful owner-App fallback, correct JWT
issuer/expiry, token refresh after expiry, and a loud message naming
`DSF_OWNER_KEYVAULT_URI` when both credentials are unavailable.

Run:

```bash
cd dotnet && dotnet test tests/Dsf.Cli.Tests/Dsf.Cli.Tests.csproj --filter "FullyQualifiedName~GitHubCharterRepositoryClientTests|FullyQualifiedName~CharterCommandTests" --no-build
```

Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add dotnet/src/Dsf.Cli/CharterRepositoryClient.cs dotnet/src/Dsf.Cli/CliApplication.cs dotnet/tests/Dsf.Cli.Tests/GitHubCharterRepositoryClientTests.cs dotnet/tests/Dsf.Cli.Tests/CharterCommandTests.cs
git commit -m "feat: authenticate charters with owner App"
```

### Task 7: Update living documentation and validate the restored path

**Files:**
- Modify: `README.md`
- Modify: `.env.example`
- Modify: `docs/site/get-started/bootstrap.md`
- Modify: `docs/site/get-started/quickstart.md`
- Modify: `docs/site/get-started/provision-a-factory.md`
- Modify: `docs/site/get-started/operate.md`
- Modify: `dotnet/tests/Dsf.Cli.Tests/LivingDocumentationTests.cs`

- [ ] **Step 1: Write failing living-documentation assertions**

Replace the no-op assertions with:

```csharp
[Fact]
public void Bootstrap_docs_describe_the_shipped_owner_provisioning()
{
    var bootstrap = ReadRepoFile("docs/site/get-started/bootstrap.md");
    var quickstart = ReadRepoFile("docs/site/get-started/quickstart.md");

    Assert.Contains("dsf bootstrap", bootstrap, StringComparison.Ordinal);
    Assert.Contains("--dry-run", bootstrap, StringComparison.Ordinal);
    Assert.Contains("App Configuration", bootstrap, StringComparison.Ordinal);
    Assert.Contains("Key Vault", bootstrap, StringComparison.Ordinal);
    Assert.DoesNotContain("not implemented", bootstrap, StringComparison.OrdinalIgnoreCase);
    Assert.DoesNotContain("not implemented", quickstart, StringComparison.OrdinalIgnoreCase);
}
```

- [ ] **Step 2: Run documentation tests to verify failure**

Run:

```bash
cd dotnet && dotnet test tests/Dsf.Cli.Tests/Dsf.Cli.Tests.csproj --filter FullyQualifiedName~LivingDocumentationTests --no-build
```

Expected: FAIL because current guides describe bootstrap as unavailable.

- [ ] **Step 3: Update operator documentation**

Document this exact safe sequence:

```bash
dsf bootstrap \
  --app-name dsf-sbx-20260907 \
  --resource-group rg-dsf-app \
  --keyvault-name kvdsfsbx20260907 \
  --appconfig-name appcsdsfsbx20260907 \
  --location swedencentral \
  --dry-run
```

Explain that a live run confirms Azure then GitHub mutations, creates the owner App
Configuration first, writes non-secrets as remote status, stores App credentials in
Key Vault, prints endpoint exports, and installs the App for selected repositories.
Update `dsf new` docs to say the owner endpoints enable automatic App identifier
resolution and per-product private-key propagation. Retain the requirement for
Azure CLI login, `GH_TOKEN`/`GITHUB_TOKEN` for repository creation, and Azure
subscription RBAC. Add owner environment variables and explicitly mark all secret
variables as secret names/pointers rather than values.

- [ ] **Step 4: Run documentation tests**

Run:

```bash
cd dotnet && dotnet test tests/Dsf.Cli.Tests/Dsf.Cli.Tests.csproj --filter FullyQualifiedName~LivingDocumentationTests --no-build
```

Expected: PASS.

- [ ] **Step 5: Run full deterministic validation**

Run:

```bash
az bicep build --file infra/owner-keyvault.bicep && \
cd dotnet && dotnet restore Dsf.sln --locked-mode && dotnet build Dsf.sln --no-restore && dotnet test Dsf.sln --no-build
```

Expected: the owner Key Vault template compiles, all projects build, and all tests pass.

- [ ] **Step 6: Commit**

```bash
git add README.md .env.example docs/site/get-started/bootstrap.md docs/site/get-started/quickstart.md docs/site/get-started/provision-a-factory.md docs/site/get-started/operate.md dotnet/tests/Dsf.Cli.Tests/LivingDocumentationTests.cs
git commit -m "docs: document owner bootstrap"
```

### Task 8: Perform live Sandbox acceptance and cleanup

**Files:**
- No repository changes required.

- [ ] **Step 1: Confirm active Azure subscription and GitHub identity**

Run:

```bash
az account show --query '{name:name,id:id}' -o json
gh auth status
```

Expected: Azure subscription is `Sandbox`; GitHub account can create a private
repository.

- [ ] **Step 2: Render the non-mutating owner-bootstrap plan**

Run:

```bash
dsf bootstrap \
  --app-name dsf-sbx-20260907 \
  --resource-group rg-dsf-app \
  --keyvault-name kvdsfsbx20260907 \
  --appconfig-name appcsdsfsbx20260907 \
  --location swedencentral \
  --dry-run
```

Expected: plan lists resource group, App Configuration, Key Vault/RBAC, status,
GitHub App, secret storage, and endpoint exports; no resources are created.

- [ ] **Step 3: Obtain explicit confirmation before live owner bootstrap**

Show the exact resource names and warn that the owner services persist. Proceed only
after an explicit operator confirmation.

- [ ] **Step 4: Run live owner bootstrap**

Run:

```bash
dsf bootstrap \
  --app-name dsf-sbx-20260907 \
  --resource-group rg-dsf-app \
  --keyvault-name kvdsfsbx20260907 \
  --appconfig-name appcsdsfsbx20260907 \
  --location swedencentral
```

Expected: the operator completes GitHub App creation/install for selected
repositories; the command reports remote completion and prints the two exports.

- [ ] **Step 5: Verify remote bootstrap evidence without revealing secrets**

Run:

```bash
az appconfig kv show \
  --endpoint "$DSF_OWNER_APPCONFIG_ENDPOINT" \
  --key "dsf/owner/bootstrap/dsf-sbx-20260907/status" \
  --auth-mode login \
  --query value -o tsv
az keyvault secret list \
  --vault-name kvdsfsbx20260907 \
  --query "[?name=='github-app-id' || name=='github-app-installation-id' || name=='github-app-private-key'].name" \
  -o tsv
```

Expected: completed non-secret status plus exactly the three credential secret
names; no secret values are requested.

- [ ] **Step 6: Render and confirm the private product-factory plan**

Run:

```bash
export DSF_OWNER_KEYVAULT_URI=https://kvdsfsbx20260907.vault.azure.net/
export DSF_OWNER_APPCONFIG_ENDPOINT=https://appcsdsfsbx20260907.azconfig.io
dsf new --product dsf-sbx-probe-20260907 --owner JoranBergfeld --visibility private \
  --name-prefix dsfsbx20260907 --location swedencentral --dry-run
```

Expected: plan resolves owner GitHub identity automatically and includes product
private-key propagation without displaying its value. Obtain a separate explicit
confirmation before rerunning without `--dry-run`.

- [ ] **Step 7: Provision and inspect one live product**

Run:

```bash
dsf new --product dsf-sbx-probe-20260907 --owner JoranBergfeld --visibility private \
  --name-prefix dsfsbx20260907 --location swedencentral --no-charter
dsf list --owner-appconfig-endpoint "$DSF_OWNER_APPCONFIG_ENDPOINT"
```

Expected: private repository, product resource group, runtime topology, SRE
deployment, App Configuration product index, and persisted manifest all exist.

- [ ] **Step 8: Explicitly confirm and remove the throwaway product**

Before deletion, show the product resource group and repository. After explicit
confirmation, execute the implemented DSF teardown command if available; otherwise
use these narrowly scoped commands:

```bash
az group delete --name rg-dsf-dsf-sbx-probe-20260907 --yes --no-wait
gh repo delete JoranBergfeld/dsf-sbx-probe-20260907 --yes
```

Expected: the product resource group deletion begins and the private repository is
gone. Keep the `rg-dsf-app` owner control-plane resources and GitHub App.

- [ ] **Step 9: Record live acceptance**

Commit no credentials or generated manifests. Record only non-secret acceptance
evidence in the session/PR discussion.

## Plan self-review

- **Spec coverage:** Tasks 1-4 restore a confirmed, remotely tracked, durable owner
  bootstrap. Tasks 5-6 cover automatic identity resolution and private-key
  propagation required for a functional runtime and charter operations. Task 7
  aligns living documentation and deterministic validation. Task 8 implements the
  approved Sandbox proof and retention/cleanup policy.
- **No placeholders:** All code-touching tasks name exact files, interfaces,
  request shapes, tests, commands, error constraints, and commit boundaries.
- **Type consistency:** `OwnerBootstrapRequest`, `OwnerBootstrapStatus`,
  `OwnerGitHubIdentity`, `IOwnerCredentialResolver`, and
  `CopyOwnerAppPrivateKeyRequest` are introduced before the tasks that consume
  them. Secret-bearing PEM values are absent from all persisted request/result
  records.
