using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Azure.Core;
using Dsf.Core.Charters;
using Dsf.Core.Onboarding;
using Dsf.Core.Runtime;
using Xunit;

namespace Dsf.Runtime.Tests;

internal sealed partial class PreviewContextTransport : HttpMessageHandler
{
    internal IntentWorkflowTransport Intent = null!;
    internal SourceInventoryTransport Source => Intent.Source;
    internal readonly Dictionary<string, JsonNode> Native = new(StringComparer.OrdinalIgnoreCase);
    internal readonly List<(string Principal, string Method, Uri Uri)> Requests = [];
    internal Action<HttpRequestMessage>? BeforeRequest { get; set; }
    internal bool EnforceCouncilScopes { get; set; }
    internal string? CapabilityETag { get; set; }
    internal Func<string, string>? ChangeContextResponse { get; set; }
    internal FactoryPreviewCapability Capability = null!;
    internal FactoryModels Models = null!;
    internal PreviewCapabilityRequest Request = null!;
    internal string Group => FactoryPreviewValidation.Group(Models);
    internal string Account => FactoryPreviewValidation.Account(Group, Source.Manifest.Identity.FactoryId);
    internal string CouncilEndpoint = "https://preview.private.azurecontainerapps.io";
    internal string ContextEndpoint = "https://context.private.azurecontainerapps.io";
    internal int ContextCalls;
    internal const string ModelEndpoint = "https://preview-models.private.azurecontainerapps.io";
    internal readonly List<Uri> ModelServiceRequests = [];
    internal Func<string, string>? ChangeModelResponse { get; set; }
    internal Func<HttpRequestMessage, HttpResponseMessage?>? ModelNativeResponse { get; set; }
    private HttpMessageInvoker inner = null!;

    private const string Charter = """
        <!-- dsf:charter profile=grounded-onboarding-v1 -->

        # Product charter

        ## Purpose
        Help coordinators coordinate volunteer shifts.

        ## Intended Users
        Volunteer coordinators arranging staffed shifts.

        ## Goals
        - Ensure scheduling prevents double booking.

        ## Non-Goals
        Unknown: No authoritative boundaries have been recorded.

        ## Success Measures
        Unknown: No authoritative measured targets are recorded.

        """;

    internal static async Task<PreviewContextTransport> CreateAsync(bool modelEvaluation = false, bool telemetryEvidence = false,
        bool mixedTables = false, bool multipleBackends = false, string? telemetryTable = null, IReadOnlyList<string>? supplementals = null,
        bool aggregateInputs = false)
    {
        var result = new PreviewContextTransport { Intent = await IntentWorkflowTransport.CreateAsync() };
        result.inner = new(result.Intent, false);
        var source = result.Source;
        source.RootTree = source.Tree((".dsf", "040000", source.Tree(("charter.md", "100644", source.Blob(Charter)))),
            ("README.md", "100644", source.Blob(IntentModelFixture.SourceText)));
        result.Intent.Models.Completion = input => IntentModelFixture.Complete(input).Replace("\"pathIndex\":1", "\"pathIndex\":2", StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.OK, (await result.Intent.RunAsync("/acquire")).Status);
        SourceHostRunner? inferred = null;
        for (var i = 0; i < 8; i++)
        {
            inferred = await result.Intent.RunAsync("/infer");
            if (inferred.Status == HttpStatusCode.OK) break;
        }
        Assert.True(inferred?.Status == HttpStatusCode.OK, inferred?.Body);
        var reviewed = await result.Intent.RunAsync("/charter-review");
        Assert.True(reviewed.Status == HttpStatusCode.OK, reviewed.Body);
        var evidence = JsonSerializer.Deserialize<CharterEvidence>(reviewed.Body, FactorySourceValidation.RecordJson)!;
        var progress = evidence.Analysis.Progress!;
        var snapshot = evidence.Source.Snapshot;
        var review = new CharterReview(snapshot.FactoryId, snapshot.RepositoryId, snapshot.Repository, snapshot.DefaultBranch,
            snapshot.Commit, snapshot.RootTree, CharterMarkdown.CharterPath, Charter, Charter, evidence.Source.Blob, evidence.Source.Blob!,
            progress.Revision, progress.ChainSha256!, progress.Snapshot.Id, progress.SourceIntegritySha256, progress.ModelConfigurationRevision,
            FactoryModelsValidation.Hash(new { source.Manifest.Identity.PlanFingerprint }), source.Manifest.Identity.GitHubActorId, true,
            FactoryModelsValidation.Hash(new { evidence.Analysis.Claims, evidence.Analysis.Limitations, evidence.Analysis.Chunks, evidence.Analysis.ExcludedPaths }), "R_913");
        var json = new JsonSerializerOptions(FactorySourceValidation.RecordJson) { IgnoreReadOnlyProperties = true };
        var payload = JsonSerializer.Serialize(new CharterPublicationPayload(review, evidence), json);
        var parts = Enumerable.Range(0, (payload.Length + 2999) / 3000).Select(i => payload.Substring(i * 3000, Math.Min(3000, payload.Length - i * 3000))).ToArray();
        var adoption = new CharterAdoption(1, Guid.NewGuid(), snapshot.FactoryId, 1, "UnchangedExisting", review.Fingerprint,
            review.AfterBlob, FactoryModelsValidation.Hash(Charter), snapshot.Commit, snapshot.RootTree, progress.Revision, progress.ChainSha256!,
            progress.Snapshot.Id, progress.SourceIntegritySha256, progress.ModelConfigurationRevision, review.OperatorId, source.Clock.Now,
            null, null, parts.Length, FactoryModelsValidation.Hash(payload));
        var operation = new CharterAdoptionOperation(adoption, null, CharterEffectState.Confirmed);
        source.Values[ProductConfigurationKeys.CharterAdopted] = JsonSerializer.Serialize(adoption, json);
        source.Values[ProductConfigurationKeys.CharterAdoptionOperation] = JsonSerializer.Serialize(operation, json);
        source.Values[ProductConfigurationKeys.CharterAdoptionReceipt(adoption.Revision, "receipt")] = JsonSerializer.Serialize(adoption, json);
        source.Values[ProductConfigurationKeys.CharterAdoptionReceipt(adoption.Revision, "intent")] = JsonSerializer.Serialize(operation with { State = CharterEffectState.Intended }, json);
        for (var i = 0; i < parts.Length; i++) source.Values[ProductConfigurationKeys.CharterAdoptionPayload(adoption.Revision, i)] = JsonSerializer.Serialize(parts[i], json);
        if (telemetryEvidence) await result.PrepareTelemetryAsync(mixedTables, multipleBackends, telemetryTable);
        if (supplementals is not null) await result.PrepareSupplementalsAsync(supplementals);
        result.Prepare(adoption.Revision, modelEvaluation, aggregateInputs);
        return result;
    }

    private void Prepare(Guid adoption, bool modelEvaluation, bool aggregateInputs)
    {
        var manifest = JsonSerializer.Deserialize<FactoryManifest>(Source.Values[ProductConfigurationKeys.FactoryManifest], FactoryGitHubJson.Options)!;
        Models = FactoryModelsValidation.ReadPreparation(manifest, Source.Values);
        var operation = Guid.NewGuid();
        var roleId = "/subscriptions/" + manifest.Identity.SubscriptionId + "/providers/microsoft.authorization/roledefinitions/cececece-cece-cece-cece-cececececece";
        var binding = Models.Bindings!;
        var governance = Models.Grants![0].Scope;
        Native[governance] = JsonSerializer.SerializeToNode(new { id = governance, properties = new
            { endpoint = Source.ProductEndpoint, provisioningState = "Succeeded", disableLocalAuth = true, publicNetworkAccess = "Enabled" } })!;
        var resources = new Dictionary<string, FoundationOperation>
        { ["council"] = binding[0], ["code"] = binding[1], ["hosting"] = binding[2] };
        foreach (var identity in binding.Take(2))
            Native[identity.Target] = JsonSerializer.SerializeToNode(new { id = identity.Target,
                systemData = new { createdAt = identity.Incarnation, lastModifiedAt = identity.Incarnation },
                properties = new { principalId = identity.PrincipalId, clientId = identity.ClientId, tenantId = manifest.Identity.TenantId } })!;
        var originals = new[] { ("account", Account, ""), ("database", FactoryPreviewValidation.Database(Account, manifest.Identity.ProductKey), "AQIDBA=="),
            ("runs", FactoryPreviewValidation.Container(Account, manifest.Identity.ProductKey, "runs"), "AQIDBAUGBwg=") };
        foreach (var (name, target, rid) in originals)
        {
            var receipt = binding[2] with { Target = target, ResourceId = target, ApiVersion = "2024-11-15", LogicalIdentity = null,
                PrincipalId = null, ClientId = null, Incarnation = rid == "" ? binding[2].Incarnation : Rid(rid) };
            resources[name] = receipt;
            Native[target] = name == "account"
                ? JsonSerializer.SerializeToNode(new { id = target, systemData = new { createdAt = receipt.Incarnation, lastModifiedAt = receipt.Incarnation },
                    properties = new { documentEndpoint = "https://" + FactoryPreviewValidation.Prefix(manifest.Identity.FactoryId) + "-cosmos.documents.azure.com:443/",
                        disableLocalAuth = true, publicNetworkAccess = "Disabled" } })!
                : StateResource(target, name == "database" ? manifest.Identity.ProductKey : "runs", rid, name != "database");
        }
        var role = JsonSerializer.SerializeToNode(new { id = roleId, properties = new { type = "CustomRole", roleName = "DSF preview metadata",
            assignableScopes = new[] { Group }, permissions = new[] { new { actions = FactoryPreviewValidation.MetadataActions,
                notActions = Array.Empty<string>(), dataActions = Array.Empty<string>(), notDataActions = Array.Empty<string>() } } } })!;
        Native[roleId] = role;
        var config = new FactoryPreviewConfiguration(1, FactoryPreviewValidation.Purpose, manifest.Identity.FactoryId, manifest.ProfilePin,
            manifest.Identity.PlanFingerprint, FactoryPreviewValidation.ManifestBasis(manifest), Source.Authority.ApprovalFingerprint,
            FactoryModelsValidation.Hash(FactorySourceValidation.Read(manifest, Source.GitHub, Source.Values).Effects),
            Models.ApprovalFingerprint, FactoryModelsValidation.Hash(Models.Effects), adoption,
            "example.azurecr.io/dsf/runtime@sha256:" + new string('7', 64), "abababab-abab-abab-abab-abababababab", roleId,
            FactoryModelsValidation.Hash(JsonSerializer.SerializeToElement(role["properties"])), resources.ToDictionary(p => p.Key, p => FactoryModelsValidation.Hash(p.Value)))
        { ModelEvaluation = modelEvaluation ? new(PreviewModelHostConfiguration.PurposeName,
            "example.azurecr.io/dsf/runtime@sha256:" + new string('8', 64), "acacacac-acac-acac-acac-acacacacacac") : null,
            Telemetry = TelemetrySelection, Supplemental = SupplementalConfiguration,
            SupplementalRevision = SupplementalConfiguration is null ? null : FactoryModelsValidation.Hash(SupplementalConfiguration),
            Inputs = aggregateInputs ? new(FactoryPreviewInputValidation.Policy, new(2, Dsf.FeatureCouncil.Conveyor.Stations.S5Council.DefaultThreshold,
                DeliberationSettings.Create().Lenses.Select(l => new CouncilLensConfiguration(l.Name, l.Weight)).ToArray())) : null };
        if (SupplementalConfiguration is not null)
            Source.Values[FactoryPreviewSupplementalValidation.ConfigurationKey(operation)] = JsonSerializer.Serialize(SupplementalConfiguration, FactoryGitHubJson.Options);
        foreach (var pair in resources)
            Source.Values[ProductConfigurationKeys.PreviewBinding(operation, pair.Key)] = JsonSerializer.Serialize(pair.Value, FactoryGitHubJson.Options);
        var created = Source.Clock.Now.ToString("O");
        Native[FactoryPreviewValidation.Container(Account, manifest.Identity.ProductKey, FactoryPreviewValidation.PreviewContainer)] =
            StateResource(FactoryPreviewValidation.Container(Account, manifest.Identity.ProductKey, FactoryPreviewValidation.PreviewContainer), FactoryPreviewValidation.PreviewContainer, "AgMEBQYHCAk=", true);
        var sqlRole = FactoryPreviewValidation.SqlRole(Account, manifest.Identity.FactoryId);
        Native[sqlRole] = JsonSerializer.SerializeToNode(new { id = sqlRole, properties = new { type = "CustomRole", roleName = "DSF preview state",
            assignableScopes = new[] { Account }, permissions = new[] { new { dataActions = FactoryPreviewValidation.StateDataActions } } } })!;
        foreach (var name in new[] { "runs", FactoryPreviewValidation.PreviewContainer })
        {
            var scope = FactoryPreviewValidation.SqlScope(Account, manifest.Identity.ProductKey, name);
            var target = FactoryPreviewValidation.SqlAssignment(Account, binding[0].PrincipalId!, scope, sqlRole);
            Native[target] = JsonSerializer.SerializeToNode(new { id = target, properties = new
                { scope, roleDefinitionId = sqlRole, principalId = binding[0].PrincipalId } })!;
        }
        foreach (var scope in FactoryPreviewValidation.MetadataScopes(manifest, Models, Source.GitHub, operation, config))
        {
            var target = FactoryModelsValidation.Grant(scope, roleId, binding[0].PrincipalId!, "").Target;
            Native[target] = JsonSerializer.SerializeToNode(new { id = target, properties = new { scope, roleDefinitionId = roleId,
                principalId = binding[0].PrincipalId, principalType = "ServicePrincipal", createdOn = created, updatedOn = created,
                createdBy = manifest.Identity.AzureActorId, condition = (string?)null, conditionVersion = (string?)null } })!;
        }
        foreach (var kind in FactoryPreviewValidation.HostKinds(config))
        {
            var projection = kind == PreviewHostKind.TelemetryEvidence ? config.Telemetry : null;
            var optional = config.Supplemental?.Hosts.SingleOrDefault(h => FactoryPreviewSupplementalValidation.HostKind(h.Kind) == kind);
            var identity = (optional is null ? null : SupplementalIdentity(optional)) ?? projection?.SourceIdentity ?? (kind == PreviewHostKind.SourceContext ? binding[1] : binding[0]);
            var model = kind == PreviewHostKind.ModelEvaluation ? config.ModelEvaluation : null;
            var target = FactoryPreviewValidation.HostTarget(Group, manifest.Identity.FactoryId, kind);
            var host = Source.Resources[Source.HostTarget].DeepClone();
            host["id"] = target;
            host["systemData"] = JsonSerializer.SerializeToNode(new { createdAt = created, lastModifiedAt = created });
            host["identity"]!["userAssignedIdentities"] = new JsonObject { [identity.Target] = new JsonObject() };
            var container = host["properties"]!["template"]!["containers"]![0]!;
            container["image"] = optional?.RuntimeImage ?? projection?.RuntimeImage ?? model?.RuntimeImage ?? config.RuntimeImage;
            container["args"] = new JsonArray(optional is not null ? "serve-preview-supplemental" : projection is not null ? "serve-preview-telemetry"
                : kind == PreviewHostKind.Council ? "serve-preview" : model is not null ? "serve-preview-models" : "serve-source-context");
            foreach (var value in container["env"]!.AsArray())
                if ((string?)value!["name"] == "AZURE_CLIENT_ID") value["value"] = identity.ClientId;
            if (optional is not null)
            {
                container["env"]!.AsArray().Add(JsonSerializer.SerializeToNode(new { name = "DSF_SUPPLEMENTAL_CONFIG",
                    value = JsonSerializer.Serialize(FactorySupplementalValidation.HostConfiguration(manifest, Supplemental!, identity, optional.Kind), FactoryGitHubJson.Options) }));
                container["env"]!.AsArray().Add(JsonSerializer.SerializeToNode(new { name = "DSF_PREVIEW_COORDINATOR",
                    value = JsonSerializer.Serialize(new PreviewSupplementalCoordinator(CouncilEndpoint, config.AudienceClientId,
                        binding[0].PrincipalId!, binding[0].ClientId!), FactoryGitHubJson.Options) }));
            }
            host["properties"]!["configuration"]!["ingress"]!["fqdn"] = new Uri(optional is not null ? SupplementalEndpoint(optional.Kind) : projection is not null ? TelemetryEndpoint
                : kind == PreviewHostKind.Council ? CouncilEndpoint : model is not null ? ModelEndpoint : ContextEndpoint).Host;
            Native[target] = host;
            var auth = Source.Resources[Source.HostTarget + "/authconfigs/current"].DeepClone();
            auth["id"] = target + "/authconfigs/current";
            auth["systemData"] = host["systemData"]!.DeepClone();
            var aad = auth["properties"]!["identityProviders"]!["azureActiveDirectory"]!;
            var audience = optional?.AudienceClientId ?? projection?.AudienceClientId ?? model?.AudienceClientId ?? config.AudienceClientId;
            aad["registration"]!["clientId"] = audience;
            aad["validation"]!["allowedAudiences"] = new JsonArray(audience, "api://" + audience);
            aad["validation"]!["defaultAuthorizationPolicy"]!["allowedPrincipals"]!["identities"] =
                JsonSerializer.SerializeToNode(kind == PreviewHostKind.Council
                    ? new[] { manifest.Identity.AzureActorId }.Concat(config.Supplemental?.Hosts.Select(h => h.PrincipalId) ?? []).ToArray()
                    : [binding[0].PrincipalId!]);
            Native[target + "/authconfigs/current"] = auth;
        }
        foreach (var optionalHost in config.Supplemental?.Hosts ?? [])
        {
            var grant = FactoryModelsValidation.Grant(optionalHost.MetadataScope, optionalHost.MetadataRoleDefinitionId, optionalHost.PrincipalId, "");
            Native[grant.Target] = JsonSerializer.SerializeToNode(new { id = grant.Target, properties = new { scope = grant.Scope,
                roleDefinitionId = grant.RoleDefinitionId, principalId = grant.PrincipalId, principalType = "ServicePrincipal",
                createdOn = created, updatedOn = created, createdBy = manifest.Identity.AzureActorId,
                condition = (string?)null, conditionVersion = (string?)null } })!;
        }
        if (config.Telemetry is { } selected)
        {
            foreach (var scope in FactoryPreviewTelemetryValidation.MetadataScopes(manifest, Models, operation, selected))
            {
                var grant = FactoryModelsValidation.Grant(scope, selected.MetadataRoleDefinitionId, selected.SourceIdentity.PrincipalId!, "");
                Native[grant.Target] = JsonSerializer.SerializeToNode(new { id = grant.Target, properties = new { scope,
                    roleDefinitionId = grant.RoleDefinitionId, principalId = grant.PrincipalId, principalType = "ServicePrincipal",
                    createdOn = created, updatedOn = created, createdBy = manifest.Identity.AzureActorId,
                    condition = (string?)null, conditionVersion = (string?)null } })!;
            }
        }
        var targets = FactoryPreviewValidation.Targets(manifest, Models, Source.GitHub, operation, roleId, config);
        var effects = targets.Select((target, i) =>
        {
            var body = JsonSerializer.SerializeToElement(Native[target.Target]);
            var hosted = target.Target.Contains("/microsoft.app/containerapps/", StringComparison.Ordinal) && !target.Target.Contains("/roleassignments/", StringComparison.Ordinal);
            var effect = new FoundationOperation(1, FactoryPreviewValidation.OperationKind, manifest.Identity.FactoryId, operation, i,
                target.Target, target.Api, new string('9', 64), Guid.NewGuid(), Source.Clock.Now, FoundationOperationState.Completed,
                FoundationResourceDisposition.Created)
            {
                ResourceId = target.Target, HttpStatus = i is >= 1 and <= 3 ? 200 : 201,
                ProviderRequestId = Guid.NewGuid().ToString(), ProviderCorrelationId = operation.ToString(), ProviderOperationId = Guid.NewGuid().ToString("N"),
                DeploymentTarget = FactoryPreviewValidation.Deployment(Group, operation),
                Incarnation = i == 0 ? Rid("AgMEBQYHCAk=") : i is >= 1 and <= 3 ? null : created,
                NativeRevision = hosted ? FactoryHostedWorkloadPolicy.ObserveExactHost(body, target.Target).Revision
                    : FactoryModelsValidation.Hash(body.GetProperty("properties")),
            };
            Source.Values[ProductConfigurationKeys.PreviewOperation(operation, i)] = JsonSerializer.Serialize(effect, FactoryGitHubJson.Options);
            Native[effect.DeploymentTarget + "/operations/" + effect.ProviderOperationId] = JsonSerializer.SerializeToNode(new
            {
                operationId = effect.ProviderOperationId, properties = new { targetResource = new { id = effect.Target },
                    provisioningOperation = "Create", provisioningState = "Succeeded", trackingId = effect.ProviderRequestId,
                    statusCode = effect.HttpStatus == 201 ? "Created" : "OK" },
            })!;
            return effect;
        }).ToArray();
        Capability = new(1, FactoryPreviewValidation.Kind, operation, FactoryModelsValidation.Hash(config), config,
            manifest.Identity.AzureActorId, Source.Clock.Now, PreviewCapabilityState.Prepared, effects.Select(FactoryModelsValidation.Hash).ToArray());
        Source.Values[ProductConfigurationKeys.FactoryPreview] = JsonSerializer.Serialize(Capability, FactoryGitHubJson.Options);
        Source.Manifest = manifest with { PreviewOperationId = operation, PreviewApprovalFingerprint = Capability.ApprovalFingerprint };
        Source.Values[ProductConfigurationKeys.FactoryManifest] = JsonSerializer.Serialize(Source.Manifest, FactoryGitHubJson.Options);
        Request = new(operation, Capability.ApprovalFingerprint, Guid.NewGuid());
    }

    internal async Task<PreviewContextRunner> RunAsync(bool coordinator = false, string? caller = null, string? client = null, object? request = null)
    {
        var requests = Requests.Count;
        var writes = Source.Writes.Count;
        var inference = Intent.Models.Inference.Count;
        var runner = new PreviewContextRunner(Source)
        {
            Caller = caller ?? (coordinator ? Source.Manifest.Identity.AzureActorId : Models.Bindings![0].PrincipalId!),
            Client = client ?? Models.Bindings![0].ClientId!, Request = request ?? Request,
            Route = coordinator ? "/source-context" : "/council-context",
        };
        using var http = new HttpClient(this, false);
        var environment = Source.Environment;
        if (coordinator) environment["AZURE_CLIENT_ID"] = Models.Bindings![0].ClientId;
        var exit = await RuntimeCliApplication.InvokeAsync([coordinator ? "serve-preview" : "serve-source-context"], environment,
            new StringWriter(), new StringWriter(), RuntimeDependencies.Production() with
            {
                WebHostRunner = runner, SourceHttp = http, SourceCredential = new SourceTestCredential(Source), SourceClock = Source.Clock,
                PreviewHttp = http, PreviewCredential = new IntentModelCredential(Source), PreviewClock = Source.Clock,
            }, CancellationToken.None);
        Assert.Equal(0, exit);
        Assert.Equal(writes, Source.Writes.Count);
        Assert.Equal(inference, Intent.Models.Inference.Count);
        Assert.DoesNotContain(Requests, r => r.Method is "PUT" or "PATCH" or "DELETE");
        Assert.DoesNotContain(Requests.Skip(requests), r => r.Method == "POST" && r.Uri.AbsolutePath is not ("/council-context" or "/authority" or "/tokens/contents"));
        return runner;
    }

    internal static async Task<PreviewContextTransport> ReconstructAsync(PreviewContextTransport first)
    {
        var worker = new PreviewContextTransport { Intent = await IntentWorkflowTransport.CreateAsync() };
        worker.inner = new(worker.Intent, false);
        worker.Source.Values = JsonSerializer.Deserialize<Dictionary<string, string>>(JsonSerializer.Serialize(first.Source.Values))!;
        worker.Source.Manifest = JsonSerializer.Deserialize<FactoryManifest>(worker.Source.Values[ProductConfigurationKeys.FactoryManifest], FactorySourceValidation.RecordJson)!;
        worker.Source.ImportProgress(first.Source);
        worker.Source.RootTree = first.Source.RootTree;
        worker.Source.Head = first.Source.Head;
        worker.Source.Clock.Now = first.Source.Clock.Now;
        worker.EnforceCouncilScopes = first.EnforceCouncilScopes;
        worker.Source.Blobs.Clear();
        foreach (var pair in first.Source.Blobs) worker.Source.Blobs[pair.Key] = Convert.FromBase64String(Convert.ToBase64String(pair.Value));
        foreach (var (before, after) in new[] { (first.Source.Trees, worker.Source.Trees), (first.Source.Resources, worker.Source.Resources),
            (first.Intent.Models.Resources, worker.Intent.Models.Resources), (first.Native, worker.Native) })
        {
            after.Clear();
            foreach (var pair in before) after[pair.Key] = JsonNode.Parse(pair.Value.ToJsonString())!;
        }
        worker.Capability = JsonSerializer.Deserialize<FactoryPreviewCapability>(worker.Source.Values[ProductConfigurationKeys.FactoryPreview], FactorySourceValidation.RecordJson)!;
        worker.Models = FactoryModelsValidation.ReadPreparation(worker.Source.Manifest, worker.Source.Values);
        if (worker.Capability.Configuration.Telemetry is not null)
        {
            worker.Telemetry = FactoryTelemetryValidation.ReadPreparation(worker.Source.Manifest, worker.Source.Values, requireChecking: false);
            worker.TelemetrySelection = worker.Capability.Configuration.Telemetry;
        }
        worker.Request = first.Request with { RequestId = Guid.NewGuid() };
        if (worker.Capability.Configuration.SupplementalRevision is not null)
        {
            worker.Supplemental = FactorySupplementalValidation.Read(worker.Source.Manifest, worker.Source.Values[ProductConfigurationKeys.FactorySupplemental]);
            worker.SupplementalConfiguration = JsonSerializer.Deserialize<PreviewSupplementalConfiguration>(
                worker.Source.Values[FactoryPreviewSupplementalValidation.ConfigurationKey(worker.Capability.OperationId)], FactorySourceValidation.RecordJson)!;
            worker.Capability = worker.Capability with { Configuration = worker.Capability.Configuration with { Supplemental = worker.SupplementalConfiguration } };
        }
        return worker;
    }

    internal void UseAccountEtag()
    {
        Native[Account].AsObject().Remove("systemData");
        Native[Account]["etag"] = "\"native-account-etag\"";
        var key = ProductConfigurationKeys.PreviewBinding(Request.OperationId, "account");
        var binding = JsonSerializer.Deserialize<FoundationOperation>(Source.Values[key], FactorySourceValidation.RecordJson)!;
        binding = binding with { HttpStatus = 201,
            Incarnation = "etag-sha256:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes("\"native-account-etag\""))) };
        ReplaceBinding("account", binding);
    }

    internal void ReplaceBinding(string name, FoundationOperation binding)
    {
        var key = ProductConfigurationKeys.PreviewBinding(Request.OperationId, name);
        Source.Values[key] = JsonSerializer.Serialize(binding, FactoryGitHubJson.Options);
        var hashes = Capability.Configuration.BindingHashes.ToDictionary();
        hashes[name] = FactoryModelsValidation.Hash(binding);
        var config = Capability.Configuration with { BindingHashes = hashes };
        Capability = Capability with { Configuration = config, ApprovalFingerprint = FactoryModelsValidation.Hash(config) };
        Source.Manifest = Source.Manifest with { PreviewApprovalFingerprint = Capability.ApprovalFingerprint };
        Source.Values[ProductConfigurationKeys.FactoryPreview] = JsonSerializer.Serialize(Capability, FactoryGitHubJson.Options);
        Source.Values[ProductConfigurationKeys.FactoryManifest] = JsonSerializer.Serialize(Source.Manifest, FactoryGitHubJson.Options);
        Request = Request with { ApprovalFingerprint = Capability.ApprovalFingerprint };
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
    {
        BeforeRequest?.Invoke(request);
        var uri = request.RequestUri!;
        var principal = TokenPrincipal(request);
        Requests.Add((principal, request.Method.Method, uri));
        if (await SupplementalRequestAsync(request, principal, token) is { } supplementalReply) return supplementalReply;
        if (uri.Host == new Uri(TelemetryEndpoint).Host)
        {
            Assert.Equal(Models.Bindings![0].PrincipalId, principal);
            Assert.Equal("/evidence", uri.AbsolutePath);
            var input = JsonSerializer.Deserialize<PreviewCapabilityRequest>(await request.Content!.ReadAsStringAsync(token), FactorySourceValidation.RecordJson)!;
            var result = await RunTelemetryAsync(request: input);
            return new(result.Status) { Content = new StringContent(ChangeTelemetryResponse?.Invoke(result.Body) ?? result.Body, Encoding.UTF8, "application/json") };
        }
        if (uri.Host == "api.loganalytics.io") return await QueryTelemetryAsync(request, principal, token);
        if (uri.Host == new Uri(ModelEndpoint).Host)
        {
            Assert.Equal(Models.Bindings![0].PrincipalId, principal);
            Assert.Equal("/evaluate", uri.AbsolutePath);
            var input = JsonSerializer.Deserialize<PreviewModelRequest>(await request.Content!.ReadAsStringAsync(token), FactorySourceValidation.RecordJson)!;
            var result = await RunModelAsync(input);
            return new(result.Status) { Content = new StringContent(ChangeModelResponse?.Invoke(result.Body) ?? result.Body, Encoding.UTF8, "application/json") };
        }
        if (uri.Host == new Uri(ContextEndpoint).Host)
        {
            Assert.Equal(Models.Bindings![0].PrincipalId, principal);
            Assert.Equal("/council-context", uri.AbsolutePath);
            ContextCalls++;
            var payload = JsonSerializer.Deserialize<PreviewCapabilityRequest>(await request.Content!.ReadAsStringAsync(token), FactorySourceValidation.RecordJson)!;
            var response = await RunAsync(request: payload);
            return new(response.Status) { Content = new StringContent(ChangeContextResponse?.Invoke(response.Body) ?? response.Body, Encoding.UTF8, "application/json") };
        }
        if (uri.Host == new Uri(Source.ProgressEndpoint).Host) Assert.Equal(Source.Code.PrincipalId, principal);
        if (uri.Host == "management.azure.com")
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Contains(principal, new[] { Source.Code.PrincipalId, Models.Bindings![0].PrincipalId, Telemetry?.Configuration.SourceIdentity.PrincipalId }
                .Concat(SupplementalHosts.Select(h => h.PrincipalId)));
            if (SupplementalHosts.SingleOrDefault(h => h.PrincipalId == principal) is { } optionalIdentity
                && !uri.AbsolutePath.StartsWith(optionalIdentity.MetadataScope + "/", StringComparison.OrdinalIgnoreCase)
                && !uri.AbsolutePath.Equals(optionalIdentity.MetadataScope, StringComparison.OrdinalIgnoreCase)) return new(HttpStatusCode.Forbidden);
            var path = uri.AbsolutePath;
            if (Telemetry is not null && principal == Telemetry.Configuration.SourceIdentity.PrincipalId
                && !FactoryPreviewTelemetryValidation.MetadataScopes(Source.Manifest, Models, Capability.OperationId, Capability.Configuration.Telemetry!)
                    .Concat(Telemetry.Configuration.AccessGrants.Select(g => g.Scope))
                    .Any(scope => path.Equals(scope, StringComparison.OrdinalIgnoreCase) || path.StartsWith(scope + "/", StringComparison.OrdinalIgnoreCase)))
                return new(HttpStatusCode.Forbidden);
            if (EnforceCouncilScopes && principal == Models.Bindings[0].PrincipalId
                && !FactoryPreviewValidation.MetadataScopes(Source.Manifest, Models, Source.GitHub, Capability.OperationId, Capability.Configuration)
                    .Any(scope => path.Equals(scope, StringComparison.OrdinalIgnoreCase) || path.StartsWith(scope + "/", StringComparison.OrdinalIgnoreCase)))
                return new(HttpStatusCode.Forbidden);
            if (TelemetryNativeResponse?.Invoke(request) is { } telemetryReply) return telemetryReply;
            if (SupplementalNativeResponse?.Invoke(request) is { } supplementalNative) return supplementalNative;
            if (path.EndsWith("/roleassignments", StringComparison.OrdinalIgnoreCase))
            {
                var scope = path[..^"/providers/microsoft.authorization/roleassignments".Length];
                var filter = Uri.UnescapeDataString(uri.Query);
                var selected = filter.Split("assignedTo('", StringSplitOptions.None)[1].Split("')", StringSplitOptions.None)[0];
                var resources = Source.Resources.Concat(Intent.Models.Resources).Concat(Native).GroupBy(p => p.Key, StringComparer.OrdinalIgnoreCase).Select(g => g.Last());
                var roles = resources.Where(p => p.Key.Contains("/providers/microsoft.authorization/roleassignments/", StringComparison.OrdinalIgnoreCase)
                    && (string?)p.Value["properties"]!["principalId"] == selected
                    && ((string?)p.Value["properties"]!["scope"] == scope
                        || scope.StartsWith((string)p.Value["properties"]!["scope"]! + "/", StringComparison.OrdinalIgnoreCase))).Select(p => p.Value).ToArray();
                return SourceInventoryTransport.Json(new { value = roles });
            }
            if (path.EndsWith("/sqlroleassignments", StringComparison.OrdinalIgnoreCase))
                return SourceInventoryTransport.Json(new { value = Native.Where(p => p.Key.StartsWith(Account + "/sqlroleassignments/", StringComparison.OrdinalIgnoreCase))
                    .Select(p => p.Value).ToArray() });
            if (Native.TryGetValue(path, out var value)) return SourceInventoryTransport.Json(value);
            if (path.Contains("/roledefinitions/", StringComparison.OrdinalIgnoreCase))
            {
                var role = Native.FirstOrDefault(p => p.Key.EndsWith("/roledefinitions/" + path.Split('/').Last(), StringComparison.OrdinalIgnoreCase));
                if (role.Value is not null) return SourceInventoryTransport.Json(role.Value);
            }
        }
        var forwarded = await inner.SendAsync(request, token);
        if (uri.Host == new Uri(Source.ProductEndpoint).Host && uri.AbsolutePath == "/kv" && CapabilityETag is not null)
        {
            var body = JsonNode.Parse(await forwarded.Content.ReadAsStringAsync(token))!;
            foreach (var setting in body["items"]!.AsArray())
                if ((string?)setting!["key"] == ProductConfigurationKeys.FactoryPreview) setting["etag"] = CapabilityETag;
            forwarded.Dispose();
            return SourceInventoryTransport.Json(body);
        }
        return forwarded;
    }

    internal async Task<PreviewContextRunner> RunModelAsync(PreviewModelRequest request, string? caller = null, string? client = null)
    {
        var writes = Source.Writes.Count;
        var runner = new PreviewContextRunner(Source) { Route = "/evaluate", Request = request,
            Caller = caller ?? Models.Bindings![0].PrincipalId!, Client = client ?? Models.Bindings![0].ClientId! };
        using var native = new ModelNativeTransport(this);
        using var http = new HttpClient(native, false);
        var environment = Source.Environment;
        environment["AZURE_CLIENT_ID"] = Models.Bindings![0].ClientId;
        Assert.Equal(0, await RuntimeCliApplication.InvokeAsync(["serve-preview-models"], environment, new StringWriter(), new StringWriter(),
            RuntimeDependencies.Production() with { WebHostRunner = runner, ModelHttp = http,
                ModelCredential = new IntentModelCredential(Source), ModelClock = Source.Clock }, CancellationToken.None));
        Assert.Equal(writes, Source.Writes.Count);
        Assert.DoesNotContain(Requests, r => r.Method is "PUT" or "PATCH" or "DELETE");
        return runner;
    }

    private sealed class ModelNativeTransport(PreviewContextTransport owner) : HttpMessageHandler
    {
        private readonly HttpMessageInvoker models = new(owner.Intent.Models, false);
        private readonly HttpMessageInvoker factory = new(owner, false);
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            owner.ModelServiceRequests.Add(request.RequestUri!);
            var uri = request.RequestUri!;
            if (owner.Models.Routes!.Any(r => uri.Host == new Uri(r.Selection.Endpoint).Host
                || uri.Host == "management.azure.com" && uri.AbsolutePath.StartsWith(r.Selection.AccountResourceId, StringComparison.OrdinalIgnoreCase)))
                return owner.ModelNativeResponse?.Invoke(request) is { } response ? Task.FromResult(response) : models.SendAsync(request, token);
            return factory.SendAsync(request, token);
        }
        protected override void Dispose(bool disposing) { if (disposing) { models.Dispose(); factory.Dispose(); } base.Dispose(disposing); }
    }

    private static string TokenPrincipal(HttpRequestMessage request)
    {
        var raw = request.Headers.Authorization?.Parameter;
        if (raw is null || !raw.Contains('.')) return "";
        var part = raw.Split('.')[1].Replace('-', '+').Replace('_', '/');
        using var body = JsonDocument.Parse(Convert.FromBase64String(part.PadRight((part.Length + 3) / 4 * 4, '=')));
        return body.RootElement.GetProperty("oid").GetString()!;
    }
    private static string Rid(string value) => "cosmos-rid-sha256:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private static JsonNode StateResource(string target, string name, string rid, bool container) =>
        JsonSerializer.SerializeToNode(new { id = target, properties = new { resource = new { id = name, _rid = rid, _etag = "\"native-etag\"",
            partitionKey = container ? new { paths = new[] { "/product" }, kind = "Hash", version = 2 } : null } } })!;
    protected override void Dispose(bool disposing) { if (disposing) { inner?.Dispose(); Intent?.Dispose(); } base.Dispose(disposing); }
}
