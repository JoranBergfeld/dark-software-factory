using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Azure.Core;
using Azure.Data.AppConfiguration;
using Dsf.Core.Onboarding;
using Dsf.Core.Runtime;

namespace Dsf.Cli;

internal sealed class FactorySourceService(IAzureCliRunner azure, HttpClient? http, TimeProvider clock)
{
    private static readonly HttpClient HostHttp = new(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(75) };

    internal async Task<FactorySource> PreviewAsync(FactoryReservationResult admission, SourceSelection selection, CancellationToken token)
    {
        FactorySourceValidation.ValidateSelection(selection);
        var store = Store(admission);
        var (_, manifest, github, values) = await ReadAsync(admission, store, token);
        if (manifest.SourceOperationId is not null || values.ContainsKey(ProductConfigurationKeys.FactorySource))
            throw new ReservationStoreException(FactoryReservationState.Blocked, "Source preparation already held; no adoption, recreation or replacement.");
        var arm = new FactoryPreparationArm(azure, http, manifest.Identity);
        var infrastructure = new List<SourceInfrastructureBinding>();
        foreach (var target in FactorySourceValidation.InfrastructureTargets(github))
        {
            var receipt = admission.Foundation!.Operations.Single(o => o.Target == target);
            using var native = await NativeAsync(arm, target, receipt.ApiVersion, token);
            FactorySourceValidation.CheckOriginalInfrastructure(native.RootElement, receipt);
            infrastructure.Add(new(receipt, FactorySourceValidation.InfrastructureIncarnation(native.RootElement, target)));
        }
        FactorySourceValidation.ValidateInfrastructure(infrastructure, github);
        var source = new FactorySource(Guid.Empty, "", new(1, manifest.Identity.FactoryId, manifest.Identity.PlanFingerprint, manifest.ProfilePin,
            github.OperationId, github.ApprovalFingerprint!, selection,
            FactoryReservationRecords.Hash(File.ReadAllBytes(new ProvisioningAssets().Resolve("onboard-source.json"))), infrastructure),
            SourceState.Partial, "preview", 0, [], null,
            "Review code MI only: private source-only App Configuration, its data owner, product data reader and factory metadata Reader; immutable code host and native Entra operator/code policy. No Cosmos/governance writer/key/model/target execution or publication. Additional Standard store/private endpoint/host costs apply.");
        source = source with { ApprovalFingerprint = FactoryModelsValidation.Hash(source.Configuration) };
        CheckCapacity(source, manifest, github);
        await CheckNativeAsync(admission, source, manifest, github, values, [], token);
        foreach (var target in FactorySourceValidation.Targets(manifest, github)) await AbsentAsync(arm, target.Target, target.Api, token);
        return source;
    }

    internal async Task<FactoryReservationResult> PrepareAsync(FactoryReservationResult admission, FactorySource preview, string approval, CancellationToken token)
    {
        if (approval != preview.ApprovalFingerprint)
            return admission with { State = FactoryReservationState.Blocked, Detail = "Source approval differs; no effects." };
        var store = Store(admission);
        ConfigurationSetting? root = null;
        FactorySource? current = null;
        var dispatched = false;
        try
        {
            var (manifestRecord, manifest, github, values) = await ReadAsync(admission, store, token);
            if (manifest.SourceOperationId is not null || values.ContainsKey(ProductConfigurationKeys.FactorySource)) throw new JsonException();
            CheckCapacity(preview, manifest, github);
            await CheckNativeAsync(admission, preview, manifest, github, values, [], token);
            var arm = new FactoryPreparationArm(azure, http, manifest.Identity);
            var targets = FactorySourceValidation.Targets(manifest, github);
            foreach (var target in targets) await AbsentAsync(arm, target.Target, target.Api, token);
            var operation = Guid.NewGuid();
            current = preview with { OperationId = operation, Stage = "preparation", OperationCount = 8 };
            var deployment = github.FactoryResourceGroupId + "/providers/microsoft.resources/deployments/sc-" + operation.ToString("N");
            await AbsentAsync(arm, deployment, "2025-04-01", token);
            var templateBytes = File.ReadAllBytes(new ProvisioningAssets().Resolve("onboard-source.json"));
            if (FactoryReservationRecords.Hash(templateBytes) != preview.Configuration.TemplateSha256) throw new JsonException();
            using var template = JsonDocument.Parse(templateBytes);
            var grants = FactorySourceValidation.Grants(manifest, github);
            var body = JsonSerializer.Serialize(new { properties = new { mode = "Incremental", template = template.RootElement, parameters = new
            {
                namePrefix = new { value = FactorySourceValidation.Prefix(github.FactoryId) }, location = new { value = admission.Plan!.Selection.FactoryLocation },
                runtimeImage = new { value = preview.Configuration.Selection.RuntimeImage }, productKey = new { value = manifest.Identity.ProductKey },
                productConfigName = new { value = "dsfo-" + github.FactoryId.ToString("N") }, progressConfigName = new { value = "dsfs-" + github.FactoryId.ToString("N") },
                profilePin = new { value = manifest.ProfilePin }, tenantId = new { value = manifest.Identity.TenantId },
                audienceClientId = new { value = preview.Configuration.Selection.AudienceClientId }, operatorPrincipalId = new { value = manifest.Identity.AzureActorId },
                readerGrantName = new { value = grants[0].Target.Split('/').Last() }, configGrantName = new { value = grants[1].Target.Split('/').Last() },
                progressGrantName = new { value = grants[2].Target.Split('/').Last() },
            } } });
            var pinned = manifest with { SourceOperationId = operation, SourceApprovalFingerprint = approval };
            await store.WriteAsync(manifestRecord.Key, FactoryReservationRecords.Serialize(pinned), manifestRecord.ETag, token);
            await CurrentAsync(pinned, github, store, null, token);
            root = await store.WriteAsync(ProductConfigurationKeys.FactorySource, FactoryReservationRecords.Serialize(current), null, token);
            var receipts = new List<ConfigurationSetting>();
            var intents = targets.Select((target, i) => new FoundationOperation(1, FactorySourceValidation.OperationKind, github.FactoryId, operation,
                i, target.Target, target.Api, FactoryReservationRecords.Hash(body), Guid.NewGuid(), clock.GetUtcNow(),
                FoundationOperationState.Intent, FoundationResourceDisposition.CreateRequested) { DeploymentTarget = deployment }).ToArray();
            foreach (var intent in intents)
            {
                await CurrentAsync(pinned, github, store, root, token);
                receipts.Add(await store.WriteAsync(ProductConfigurationKeys.SourceOperation(operation, intent.Index), FactoryReservationRecords.Serialize(intent), null, token));
            }
            await CheckNativeAsync(admission, preview, pinned, github, values, [], token);
            foreach (var target in targets) await AbsentAsync(arm, target.Target, target.Api, token);
            await CurrentAsync(pinned, github, store, root, token);
            dispatched = true;
            using var response = await arm.SendAsync(HttpMethod.Put, deployment, "2025-04-01", token, body, intents[0].ClientRequestId);
            if (response.StatusCode != HttpStatusCode.Created) throw new JsonException("Affirmative create required.");
            using var deployed = await DocumentAsync(response, token);
            var native = deployed.RootElement;
            var correlation = Guid.Parse(native.GetProperty("properties").GetProperty("correlationId").GetString()!);
            for (var poll = 0; native.GetProperty("properties").GetProperty("provisioningState").GetString() is "Running" or "Accepted" or "Creating" && poll < 120; poll++)
            {
                await Task.Delay(TimeSpan.FromSeconds(10), clock, token);
                await CurrentAsync(pinned, github, store, root, token);
                using var observed = await NativeAsync(arm, deployment, "2025-04-01", token);
                native = observed.RootElement.Clone();
                if (native.GetProperty("properties").GetProperty("correlationId").GetString() != correlation.ToString()) throw new JsonException();
            }
            if (!string.Equals(native.GetProperty("id").GetString(), deployment, StringComparison.OrdinalIgnoreCase)
                || correlation == Guid.Empty || native.GetProperty("properties").GetProperty("provisioningState").GetString() != "Succeeded") throw new JsonException();
            using var operations = await NativeAsync(arm, deployment + "/operations", "2025-04-01", token);
            var operationReceipts = Complete(operations.RootElement);
            if (operationReceipts.Length != targets.Count) throw new JsonException();
            var effects = new List<FoundationOperation>();
            foreach (var intent in intents)
            {
                await CurrentAsync(pinned, github, store, root, token);
                var receipt = operationReceipts.Single(r => string.Equals(r.GetProperty("properties").GetProperty("targetResource").GetProperty("id").GetString(),
                    intent.Target, StringComparison.OrdinalIgnoreCase));
                var properties = receipt.GetProperty("properties");
                var providerOperation = receipt.GetProperty("operationId").GetString();
                if (!FactoryTelemetryValidation.IsProviderOperationId(providerOperation)
                    || properties.GetProperty("provisioningOperation").GetString() != "Create"
                    || properties.GetProperty("provisioningState").GetString() != "Succeeded"
                    || properties.GetProperty("statusCode").GetString() != "Created") throw new JsonException();
                var tracking = Guid.Parse(properties.GetProperty("trackingId").GetString()!);
                if (tracking == Guid.Empty) throw new JsonException();
                using var resource = await NativeAsync(arm, intent.Target, intent.ApiVersion, token);
                var incarnation = FactorySourceValidation.ObserveResource(resource.RootElement, intent.Target, pinned, github);
                if (!FactorySourceValidation.CreationIncarnation(intent with { Incarnation = incarnation })
                    || DateTimeOffset.TryParse(incarnation, System.Globalization.CultureInfo.InvariantCulture, out var created)
                        && created > clock.GetUtcNow()) throw new JsonException();
                var effect = intent with
                {
                    State = FoundationOperationState.Completed, Disposition = FoundationResourceDisposition.Created, ResourceId = intent.Target,
                    HttpStatus = 201, Incarnation = incarnation, NativeRevision = FactorySourceValidation.NativeRevision(resource.RootElement, intent.Target),
                    ProviderRequestId = tracking.ToString(), ProviderCorrelationId = correlation.ToString(), ProviderOperationId = providerOperation,
                };
                await CurrentAsync(pinned, github, store, root, token);
                await store.WriteAsync(receipts[intent.Index].Key, FactoryReservationRecords.Serialize(effect), receipts[intent.Index].ETag, token);
                effects.Add(effect);
            }
            var endpoint = await CheckNativeAsync(admission, current, pinned, github, values, effects, token);
            current = current with { Stage = "prepared", EffectHashes = effects.Select(FactoryModelsValidation.Hash).ToArray(), HostEndpoint = endpoint,
                Detail = "Source workload prepared. Run source to acquire pinned inventory; no source read, analysis or activation asserted by preparation." };
            await CurrentAsync(pinned, github, store, root, token);
            root = await store.WriteAsync(ProductConfigurationKeys.FactorySource, FactoryReservationRecords.Serialize(current), root.ETag, token);
            await CurrentAsync(pinned, github, store, root, token);
            return admission with { State = FactoryReservationState.Partial, Source = current, HasRegistryEffects = true, Detail = current.Detail };
        }
        catch (OperationCanceledException)
        { return admission with { State = FactoryReservationState.Canceled, Source = current, Detail = "Canceled; source preparation/hold unconfirmed. Inspect same operation; never recreate or roll back." }; }
        catch (Exception e) when (e is JsonException or InvalidOperationException or ArgumentException or KeyNotFoundException or HttpRequestException
            or ReservationStoreException or OwnerReservationStore.AuthenticationFailedException)
        {
            var detail = dispatched ? "Source deployment outcome held; original operations require inspection, never recreate or roll back."
                : "Source preparation held before deployment; no workload authority. Inspect original intent.";
            if (root is not null && current is not null)
            {
                current = current with { State = SourceState.Unknown, Stage = "held", Detail = detail };
                try { await store.WriteAsync(root.Key, FactoryReservationRecords.Serialize(current), root.ETag, token); }
                catch (ReservationStoreException) { detail = "Final source hold unconfirmed; inspect trusted product records. No recreation or revocation asserted."; }
            }
            return admission with { State = FactoryReservationState.Unknown, Source = current, Detail = detail, HasRegistryEffects = true };
        }
    }

    internal async Task<SourceInventoryResult> AcquireAsync(FactoryReservationResult admission, SourceAcquireRequest request, bool status, CancellationToken token)
    {
        if (request.Page < 0 || request.Page > FactorySourceValidation.MaximumNodes / FactorySourceValidation.PageSize) throw new JsonException();
        var store = Store(admission);
        var (_, manifest, github, values) = await ReadAsync(admission, store, token);
        var (source, effects) = FactorySourceValidation.Read(manifest, github, values);
        await CheckNativeAsync(admission, source, manifest, github, values, effects, token);
        var root = await store.ReadAsync(ProductConfigurationKeys.FactorySource, token) ?? throw new JsonException();
        var credential = new OwnerReservationStore.OperatorCredential(azure, manifest.Identity.TenantId, manifest.Identity.SubscriptionId,
            manifest.Identity.AzureActorId, "api://" + source.Configuration.Selection.AudienceClientId);
        var access = await credential.GetTokenAsync(new TokenRequestContext(["api://" + source.Configuration.Selection.AudienceClientId + "/.default"]), token);
        using var call = new HttpRequestMessage(HttpMethod.Post, source.HostEndpoint + (status ? "/status" : "/acquire"))
        { Content = JsonContent.Create(request, options: FactoryGitHubJson.Options) };
        call.Headers.Authorization = new AuthenticationHeaderValue("Bearer", access.Token);
        await CurrentAsync(manifest, github, store, root, token);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(75), clock);
        using var pending = CancellationTokenSource.CreateLinkedTokenSource(token, deadline.Token);
        try
        {
            using var response = await (http ?? HostHttp).SendAsync(call, HttpCompletionOption.ResponseHeadersRead, pending.Token);
            if (response.StatusCode is not (HttpStatusCode.OK or HttpStatusCode.Conflict or HttpStatusCode.Forbidden)) throw new JsonException();
            if (response.Content.Headers.ContentLength > 512 * 1024) throw new JsonException();
            await using var stream = await response.Content.ReadAsStreamAsync(pending.Token);
            using var buffer = new MemoryStream();
            var bytes = new byte[8192];
            while (true)
            {
                var count = await stream.ReadAsync(bytes, pending.Token);
                if (count == 0) break;
                if (buffer.Length + count > 512 * 1024) throw new JsonException();
                buffer.Write(bytes, 0, count);
            }
            var result = JsonSerializer.Deserialize<SourceInventoryResult>(buffer.ToArray(), FactorySourceValidation.RecordJson) ?? throw new JsonException();
            if (!Enum.IsDefined(result.State) || result.Analysis != "NotYetAnalyzed" || result.RawSourceRetained || result.DeployedSourceCorrelation != "Unknown"
                || result.Progress is { } progress && (progress.Snapshot.FactoryId != manifest.Identity.FactoryId || progress.Snapshot.RepositoryId != github.RepositoryId
                    || progress.Snapshot.AccountId != github.AccountId || progress.Snapshot.OperationId != source.OperationId
                    || progress.Snapshot.ConfigurationRevision != source.ApprovalFingerprint
                    || !FactorySourceValidation.GitId(progress.Snapshot.Commit) || !FactorySourceValidation.GitId(progress.Snapshot.RootTree))
                || result.State == SourceState.Acquired && (response.StatusCode != HttpStatusCode.OK
                    || result.Progress is not { CompleteRootTree: true, NecessaryGaps: 0, Failed: 0, Worker: null }))
                throw new JsonException("Invalid source coverage response.");
            await CheckNativeAsync(admission, source, manifest, github, values, effects, pending.Token);
            await CurrentAsync(manifest, github, store, root, pending.Token);
            return result;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        { return new(SourceState.Canceled, "Canceled; remote acquisition outcome/hold unconfirmed. Inspect trusted progress.", null, [], null); }
        catch (Exception e) when (e is HttpRequestException or IOException or OperationCanceledException or JsonException
            or InvalidOperationException or ArgumentException or KeyNotFoundException or ReservationStoreException)
        { return new(SourceState.Unknown, "Source response or final authority unconfirmed; inspect trusted remote progress, never recreate infrastructure.", null, [], null); }
    }

    private async Task<string?> CheckNativeAsync(FactoryReservationResult admission, FactorySource source, FactoryManifest manifest,
        FactoryGitHubAuthority github, IReadOnlyDictionary<string, string> values, IReadOnlyList<FoundationOperation> effects, CancellationToken token)
    {
        var arm = new FactoryPreparationArm(azure, http, manifest.Identity);
        FactorySourceValidation.ValidateInfrastructure(source.Configuration.Infrastructure, github);
        foreach (var binding in source.Configuration.Infrastructure)
        {
            using var native = await NativeAsync(arm, binding.Receipt.Target, binding.Receipt.ApiVersion, token);
            if (effects.Count == 0) FactorySourceValidation.CheckOriginalInfrastructure(native.RootElement, binding.Receipt);
            if (FactorySourceValidation.InfrastructureIncarnation(native.RootElement, binding.Receipt.Target) != binding.Incarnation)
                throw new JsonException("Approved foundation network/DNS incarnation changed.");
        }
        var bindings = new[] { "code", "hosting" }.Select(n => FactorySourceValidation.ReadRecord<FoundationOperation>(values,
            ProductConfigurationKeys.GitHubAuthorityBinding(github.OperationId, n))).ToArray();
        foreach (var binding in bindings)
        {
            using var native = await NativeAsync(arm, binding.Target, binding.ApiVersion, token);
            if (!string.Equals(native.RootElement.GetProperty("id").GetString(), binding.Target, StringComparison.OrdinalIgnoreCase)
                || native.RootElement.GetProperty("systemData").GetProperty("createdAt").GetDateTimeOffset()
                    != DateTimeOffset.Parse(binding.Incarnation!, System.Globalization.CultureInfo.InvariantCulture)) throw new JsonException();
            var properties = native.RootElement.GetProperty("properties");
            if (binding.LogicalIdentity is not null && (properties.GetProperty("principalId").GetString() != binding.PrincipalId
                || properties.GetProperty("clientId").GetString() != binding.ClientId || properties.GetProperty("tenantId").GetString() != manifest.Identity.TenantId)) throw new JsonException();
            if (binding == bindings[1] && properties.GetProperty("vnetConfiguration").GetProperty("internal").ValueKind != JsonValueKind.True) throw new JsonException();
        }
        var grants = FactorySourceValidation.Grants(manifest, github);
        foreach (var grant in grants)
        {
            using var role = await NativeAsync(arm, grant.RoleDefinitionId, "2022-04-01", token);
            FactorySourceValidation.ValidateRole(role.RootElement, grant);
            if (effects.Count == 0)
            {
                // Missing progress store is created in the same deployment; delegation must exist at its parent.
                var scope = grant.Scope == FactorySourceValidation.ProgressTarget(github) ? github.FactoryResourceGroupId! : grant.Scope;
                using var permissions = await NativeAsync(arm, scope + "/providers/microsoft.authorization/permissions", "2022-04-01", token);
                using var denies = await NativeAsync(arm, scope + "/providers/microsoft.authorization/denyassignments", "2022-04-01", token);
                if (Complete(denies.RootElement).Length != 0 || !Complete(permissions.RootElement).Any(p =>
                    FactoryModelsValidation.Strings(p, "actions").Any(a => a is "*" or "Microsoft.Authorization/*" or "Microsoft.Authorization/roleAssignments/write")
                    && FactoryModelsValidation.Strings(p, "notActions").Length == 0)) throw new JsonException("Exact source grantor prerequisite missing.");
            }
        }
        using (var roles = await NativeAsync(arm, "/subscriptions/" + manifest.Identity.SubscriptionId
            + "/providers/microsoft.authorization/roleassignments?$filter=assignedTo('" + github.CodePrincipalId + "')", "2022-04-01", token))
        {
            var current = Complete(roles.RootElement);
            if (current.Length != (effects.Count == 0 ? 0 : grants.Count) || current.Any(r => !grants.Any(g =>
                string.Equals(g.Target, r.GetProperty("id").GetString(), StringComparison.OrdinalIgnoreCase)))) throw new JsonException("Unreviewed source authority; administrator must reconcile.");
        }
        var resources = new Dictionary<string, JsonElement>();
        foreach (var effect in effects)
        {
            using var native = await NativeAsync(arm, effect.Target, effect.ApiVersion, token);
            if (FactorySourceValidation.ObserveResource(native.RootElement, effect.Target, manifest, github) != effect.Incarnation
                || FactorySourceValidation.NativeRevision(native.RootElement, effect.Target) != effect.NativeRevision) throw new JsonException("Current source effect differs from original receipt.");
            resources.Add(effect.Target, native.RootElement.Clone());
        }
        if (effects.Count == 0) return null;
        var target = FactorySourceValidation.HostTarget(github);
        var endpoint = FactoryHostedWorkloadPolicy.Validate(resources[target], resources[target + "/authconfigs/current"], bindings[0], bindings[1],
            source.Configuration.Selection.RuntimeImage, "serve-source", manifest.Identity.ProductKey, admission.Foundation!.ProductEndpoint!, manifest.ProfilePin,
            manifest.Identity.TenantId, source.Configuration.Selection.AudienceClientId, [manifest.Identity.AzureActorId, github.CodePrincipalId]);
        if (source.HostEndpoint is not null && source.HostEndpoint != endpoint) throw new JsonException();
        return endpoint;
    }

    internal static async Task<FactoryReservationResult> InspectAsync(FactoryReservationResult admission, OwnerReservationStore store, CancellationToken token)
    {
        var manifestRecord = await store.ReadAsync(ProductConfigurationKeys.FactoryManifest, token);
        var manifest = FactoryReservationRecords.Parse<FactoryManifest>(manifestRecord?.Value ?? throw new JsonException());
        if (manifest.SourceOperationId is null) return admission;
        var root = await store.ReadAsync(ProductConfigurationKeys.FactorySource, token);
        if (root is null) return admission with { State = FactoryReservationState.Unknown, Detail = "Source intent held without authority; no automatic retry." };
        var values = await ValuesAsync(store, manifest, token);
        var github = FactoryGitHubAuthorityValidation.ReadPreparation(manifest, values);
        var source = FactorySourceValidation.ReadRecord<FactorySource>(values, ProductConfigurationKeys.FactorySource);
        var read = FactorySourceValidation.Read(manifest, github, values, requirePrepared: source.Stage == "prepared");
        return admission with { Source = read.Source };
    }

    private async Task<(ConfigurationSetting Record, FactoryManifest Manifest, FactoryGitHubAuthority GitHub, Dictionary<string, string> Values)> ReadAsync(
        FactoryReservationResult admission, OwnerReservationStore store, CancellationToken token)
    {
        var record = await store.ReadAsync(ProductConfigurationKeys.FactoryManifest, token) ?? throw new JsonException();
        var profile = await store.ReadAsync(ProductConfigurationKeys.FactoryProfile, token);
        var manifest = FactoryGovernance.ReadInactiveManifest(record.Value, profile?.Value, admission.Identity!.ProductKey,
            admission.Foundation!.ProductEndpoint!, admission.Foundation.ProfilePin);
        if (manifest.Identity != admission.Identity || admission.Foundation.State != FactoryFoundationState.Inactive) throw new JsonException();
        var values = await ValuesAsync(store, manifest, token);
        var github = FactoryGitHubAuthorityValidation.ReadPreparation(manifest, values);
        FactoryGitHubAuthorityValidation.ReadCompletedEffects(github, values);
        FactoryGitHubAuthorityValidation.ValidateVerified(github);
        return (record, manifest, github, values);
    }
    private async Task CurrentAsync(FactoryManifest manifest, FactoryGitHubAuthority github, OwnerReservationStore store, ConfigurationSetting? root, CancellationToken token)
    {
        var current = await store.ReadAsync(ProductConfigurationKeys.FactoryManifest, token);
        var profile = await store.ReadAsync(ProductConfigurationKeys.FactoryProfile, token);
        var loaded = FactoryGovernance.ReadInactiveManifest(current?.Value, profile?.Value, manifest.Identity.ProductKey,
            "https://dsfo-" + manifest.Identity.FactoryId.ToString("N") + ".azconfig.io", manifest.ProfilePin);
        if (FactoryModelsValidation.Hash(loaded) != FactoryModelsValidation.Hash(manifest)) throw new JsonException();
        var values = await ValuesAsync(store, manifest, token);
        var latest = FactoryGitHubAuthorityValidation.ReadPreparation(loaded, values);
        FactoryGitHubAuthorityValidation.ReadCompletedEffects(latest, values);
        FactoryGitHubAuthorityValidation.ValidateVerified(latest);
        if (FactoryModelsValidation.Hash(latest) != FactoryModelsValidation.Hash(github)) throw new JsonException();
        if (root is not null)
        {
            var record = await store.ReadAsync(root.Key, token);
            if (record?.ETag != root.ETag || record.Value != root.Value) throw new JsonException();
        }
    }
    private static async Task<Dictionary<string, string>> ValuesAsync(OwnerReservationStore store, FactoryManifest manifest, CancellationToken token)
    {
        var github = manifest.GitHubAuthorityOperationId ?? throw new JsonException();
        var keys = new[] { ProductConfigurationKeys.FactoryGitHubAuthority, ProductConfigurationKeys.FactorySource }
            .Concat(FactoryGitHubAuthorityValidation.BindingNames.Select(n => ProductConfigurationKeys.GitHubAuthorityBinding(github, n)))
            .Concat(Enumerable.Range(0, 5).Select(i => ProductConfigurationKeys.GitHubAuthorityOperation(github, i)))
            .Concat(manifest.SourceOperationId is { } source ? Enumerable.Range(0, 8).Select(i => ProductConfigurationKeys.SourceOperation(source, i)) : []);
        var values = new Dictionary<string, string>();
        foreach (var key in keys) if (await store.ReadAsync(key, token) is { } record) values.Add(key, record.Value);
        return values;
    }
    private OwnerReservationStore Store(FactoryReservationResult admission)
    {
        if (admission.State != FactoryReservationState.Reserved || admission.Identity is null || admission.Foundation?.State != FactoryFoundationState.Inactive)
            throw new ReservationStoreException(FactoryReservationState.Blocked, "Proven inactive factory and verified readonly GitHub authority required; run source --prepare before acquisition.");
        return new(admission.Foundation.ProductEndpoint!, admission.Identity.TenantId, admission.Identity.SubscriptionId, azure, http, admission.Identity.AzureActorId);
    }
    private static void CheckCapacity(FactorySource source, FactoryManifest manifest, FactoryGitHubAuthority github)
    {
        OwnerReservationStore.CheckSize(ProductConfigurationKeys.FactorySource, ProductConfigurationKeys.NoLabel, FactoryReservationRecords.Serialize(source with
            { OperationCount = 8, EffectHashes = Enumerable.Repeat(new string('f', 64), 8).ToArray(), HostEndpoint = new string('x', 261), Detail = new string('x', 1024) }));
        OwnerReservationStore.CheckSize(ProductConfigurationKeys.FactoryManifest, ProductConfigurationKeys.NoLabel,
            FactoryReservationRecords.Serialize(manifest with { SourceOperationId = Guid.Empty, SourceApprovalFingerprint = new string('f', 64) }));
        foreach (var target in FactorySourceValidation.Targets(manifest, github))
        {
            var record = new FoundationOperation(1, FactorySourceValidation.OperationKind, github.FactoryId, Guid.Empty, 7,
                target.Target, target.Api, new string('f', 64), Guid.Empty, DateTimeOffset.MaxValue, FoundationOperationState.Completed, FoundationResourceDisposition.Created)
            { ResourceId = target.Target, NativeRevision = new string('f', 64), Incarnation = DateTimeOffset.MaxValue.ToString("O"),
                ProviderRequestId = Guid.Empty.ToString(), ProviderCorrelationId = Guid.Empty.ToString(), ProviderOperationId = new string('f', 128),
                DeploymentTarget = github.FactoryResourceGroupId + "/providers/microsoft.resources/deployments/sc-" + Guid.Empty.ToString("N") };
            OwnerReservationStore.CheckSize(ProductConfigurationKeys.SourceOperation(Guid.Empty, 7), ProductConfigurationKeys.NoLabel, FactoryReservationRecords.Serialize(record));
        }
    }
    private static async Task<JsonDocument> NativeAsync(FactoryPreparationArm arm, string target, string api, CancellationToken token)
    {
        using var response = await arm.SendAsync(HttpMethod.Get, target, api, token);
        if (response.StatusCode != HttpStatusCode.OK) throw new JsonException("Required source provider metadata unavailable.");
        return await DocumentAsync(response, token);
    }
    private static async Task AbsentAsync(FactoryPreparationArm arm, string target, string api, CancellationToken token)
    {
        using var response = await arm.SendAsync(HttpMethod.Get, target, api, token);
        if (response.StatusCode != HttpStatusCode.NotFound) throw new JsonException("Source create target is not proven absent; no adoption.");
    }
    private static async Task<JsonDocument> DocumentAsync(HttpResponseMessage response, CancellationToken token) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync(token), new JsonDocumentOptions { AllowDuplicateProperties = false });
    private static JsonElement[] Complete(JsonElement response)
    {
        if (response.TryGetProperty("nextLink", out var next) && next.ValueKind != JsonValueKind.Null && next.GetString() != "") throw new JsonException("Incomplete native inventory.");
        return response.GetProperty("value").EnumerateArray().ToArray();
    }
}
