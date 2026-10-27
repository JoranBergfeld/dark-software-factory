using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Dsf.Core.Runtime;

namespace Dsf.Core.Onboarding;

public static class FactorySourceValidation
{
    public static readonly JsonSerializerOptions RecordJson = new(FactoryGitHubJson.Options)
    { RespectNullableAnnotations = true, RespectRequiredConstructorParameters = true };
    public const string OperationKind = "decide-source-operation";
    public const string ReaderRole = "acdd72a7-3385-48ef-bd42-f606fba81ae7";
    public const string ProgressWriterRole = "5ae67dd6-50cb-40e7-96ff-dc2bfa4b606b";
    public const int RecordBytes = 8192;
    public const int MaximumNodes = 32768;
    public const int MaximumRecords = 65536;
    public const long MaximumMetadataBytes = 64 * 1024 * 1024;
    public const int MaximumBlobBytes = 256 * 1024;
    public const int ChunkBytes = 16 * 1024;
    public const int PageSize = 32;
    public const int MaximumChunksPerAdvance = 4;
    public const int MaximumNodesPerAdvance = 32;
    public const int MaximumTreeBytes = 4 * 1024 * 1024;
    public static readonly TimeSpan AdvanceBudget = TimeSpan.FromSeconds(45);
    public static readonly TimeSpan LeaseBudget = TimeSpan.FromSeconds(60);

    public static string Prefix(Guid factory) => "df-" + factory.ToString("N")[..14];
    public static string ProgressEndpoint(Guid factory) => "https://dsfs-" + factory.ToString("N") + ".azconfig.io";
    public static string ProgressTarget(FactoryGitHubAuthority github) => github.FactoryResourceGroupId
        + "/providers/microsoft.appconfiguration/configurationstores/dsfs-" + github.FactoryId.ToString("N");
    public static string HostTarget(FactoryGitHubAuthority github) => github.FactoryResourceGroupId
        + "/providers/microsoft.app/containerapps/" + Prefix(github.FactoryId) + "-source";
    public static IReadOnlyList<ModelAccessGrant> Grants(FactoryManifest manifest, FactoryGitHubAuthority github) =>
        new[] { (github.FactoryResourceGroupId!, ReaderRole),
            (github.FactoryResourceGroupId + "/providers/microsoft.appconfiguration/configurationstores/dsfo-" + github.FactoryId.ToString("N"), FactoryModelsValidation.ConfigReaderRole),
            (ProgressTarget(github), ProgressWriterRole) }
        .Select(g => FactoryModelsValidation.Grant(g.Item1,
            "/subscriptions/" + manifest.Identity.SubscriptionId + "/providers/microsoft.authorization/roledefinitions/" + g.Item2,
            github.CodePrincipalId, "")).ToArray();

    public static IReadOnlyList<(string Target, string Api)> Targets(FactoryManifest manifest, FactoryGitHubAuthority github)
    {
        var endpoint = github.FactoryResourceGroupId + "/providers/microsoft.network/privateendpoints/" + Prefix(github.FactoryId) + "-source-config";
        return new[] { (ProgressTarget(github), "2024-06-01"), (endpoint, "2024-05-01"),
            (endpoint + "/privatednszonegroups/default", "2024-05-01") }
            .Concat(Grants(manifest, github).Select(g => (g.Target, "2022-04-01")))
            .Concat([(HostTarget(github), "2025-01-01"), (HostTarget(github) + "/authconfigs/current", "2025-01-01")]).ToArray();
    }

    public static void ValidateSelection(SourceSelection selection)
    {
        if (selection.RuntimeImage.Length > 512 || !Regex.IsMatch(selection.RuntimeImage, "^[a-z0-9.-]+/[a-zA-Z0-9_./-]+@sha256:[0-9a-f]{64}$")
            || !Guid.TryParse(selection.AudienceClientId, out var id) || id == Guid.Empty)
            throw new JsonException("Immutable runtime image and existing native Entra API registration required.");
    }

    public static (FactorySource Source, FoundationOperation[] Effects) Read(
        FactoryManifest manifest, FactoryGitHubAuthority github, IReadOnlyDictionary<string, string> values, bool requirePrepared = true)
    {
        var source = ReadRecord<FactorySource>(values, ProductConfigurationKeys.FactorySource);
        var config = source.Configuration;
        ValidateSelection(config.Selection);
        ValidateInfrastructure(config.Infrastructure, github);
        if (source.OperationId == Guid.Empty || manifest.SourceOperationId != source.OperationId
            || manifest.SourceApprovalFingerprint != source.ApprovalFingerprint
            || source.ApprovalFingerprint != FactoryModelsValidation.Hash(config)
            || config.SchemaVersion != 1 || config.FactoryId != manifest.Identity.FactoryId
            || config.PlanFingerprint != manifest.Identity.PlanFingerprint || config.ProfilePin != manifest.ProfilePin
            || config.GitHubOperationId != github.OperationId || config.GitHubApprovalFingerprint != github.ApprovalFingerprint
            || !Hash(config.TemplateSha256) || source.OperationCount is < 0 or > 8
            || requirePrepared && (source.Stage != "prepared" || source.State != SourceState.Partial || source.OperationCount != 8 || source.EffectHashes.Count != 8))
            throw new JsonException("Current approved inactive source acquisition authority required.");
        var targets = Targets(manifest, github);
        var effects = Enumerable.Range(0, source.OperationCount).Select(i =>
        {
            var effect = ReadRecord<FoundationOperation>(values, ProductConfigurationKeys.SourceOperation(source.OperationId, i));
            if (effect.Kind != OperationKind || effect.SchemaVersion != 1 || effect.FactoryId != config.FactoryId
                || effect.OperationId != source.OperationId || effect.Index != i || effect.Target != targets[i].Target
                || effect.ApiVersion != targets[i].Api || effect.ClientRequestId == Guid.Empty || !Hash(effect.RequestSha256)
                || effect.DeploymentTarget != github.FactoryResourceGroupId + "/providers/microsoft.resources/deployments/sc-" + source.OperationId.ToString("N")
                || requirePrepared && (effect.State != FoundationOperationState.Completed || effect.Disposition != FoundationResourceDisposition.Created
                    || effect.ResourceId != effect.Target || effect.HttpStatus != 201 || !Hash(effect.NativeRevision)
                    || !Guid.TryParse(effect.ProviderRequestId, out var request) || request == Guid.Empty
                    || !Guid.TryParse(effect.ProviderCorrelationId, out var correlation) || correlation == Guid.Empty
                    || !FactoryTelemetryValidation.IsProviderOperationId(effect.ProviderOperationId)
                    || !CreationIncarnation(effect)
                    || FactoryModelsValidation.Hash(effect) != source.EffectHashes[i]))
                throw new JsonException("Complete original source creation receipts required; effects remain held.");
            return effect;
        }).ToArray();
        return (source, effects);
    }

    public static string[] InfrastructureTargets(FactoryGitHubAuthority github) =>
        [github.FactoryResourceGroupId + "/providers/microsoft.network/virtualnetworks/" + Prefix(github.FactoryId) + "-network",
            github.FactoryResourceGroupId + "/providers/microsoft.network/privatednszones/privatelink.azconfig.io"];

    public static void ValidateInfrastructure(IReadOnlyList<SourceInfrastructureBinding> bindings, FactoryGitHubAuthority github)
    {
        if (bindings.Count != 2) throw new JsonException("Original foundation network and DNS bindings required.");
        var targets = InfrastructureTargets(github);
        for (var i = 0; i < bindings.Count; i++)
        {
            var receipt = bindings[i].Receipt;
            if (receipt.FactoryId != github.FactoryId || receipt.OperationId != github.FoundationOperationId
                || receipt.Kind != "decide-foundation-operation" || receipt.SchemaVersion != 1
                || receipt.Target != targets[i] || receipt.ResourceId != targets[i]
                || receipt.ApiVersion != (i == 0 ? "2024-05-01" : "2024-06-01")
                || receipt.State != FoundationOperationState.Completed || receipt.Disposition != FoundationResourceDisposition.Created
                || !Hash(receipt.RequestSha256) || string.IsNullOrEmpty(receipt.ProviderOperationId)
                || string.IsNullOrEmpty(receipt.Incarnation) || string.IsNullOrEmpty(bindings[i].Incarnation))
                throw new JsonException("Original foundation creation receipt required; no name-based network adoption.");
        }
    }

    public static void CheckOriginalInfrastructure(JsonElement native, FoundationOperation receipt)
    {
        if (!Same(native.GetProperty("id").GetString(), receipt.Target)) throw new JsonException();
        var observed = receipt.Incarnation!.StartsWith("etag-sha256:", StringComparison.Ordinal)
            ? "etag-sha256:" + TextHash(native.GetProperty("etag").GetString()!)
            : native.GetProperty("systemData").GetProperty("createdAt").GetDateTimeOffset().ToUniversalTime().ToString("O");
        if (observed != receipt.Incarnation) throw new JsonException("Foundation infrastructure incarnation changed.");
    }

    public static string InfrastructureIncarnation(JsonElement native, string target)
    {
        if (!Same(native.GetProperty("id").GetString(), target)) throw new JsonException();
        if (target.Contains("/virtualnetworks/", StringComparison.Ordinal)
            && native.GetProperty("properties").TryGetProperty("resourceGuid", out var value))
        {
            var id = value.GetGuid();
            if (id == Guid.Empty) throw new JsonException();
            return "resource-guid:" + id;
        }
        return native.TryGetProperty("systemData", out var system)
            ? system.GetProperty("createdAt").GetDateTimeOffset().ToUniversalTime().ToString("O")
            : "etag-sha256:" + TextHash(native.GetProperty("etag").GetString()!);
    }

    private static string TextHash(string text) => Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    public static T ReadRecord<T>(IReadOnlyDictionary<string, string> values, string key)
    {
        var text = values.GetValueOrDefault(key) ?? throw new JsonException("Required provenance record missing.");
        if (Encoding.UTF8.GetByteCount(key + text) + 1024 >= 10 * 1024) throw new JsonException("Provenance record exceeds service capacity.");
        return JsonSerializer.Deserialize<T>(text, RecordJson) ?? throw new JsonException();
    }

    public static string ObserveResource(JsonElement resource, string target, FactoryManifest manifest, FactoryGitHubAuthority github)
    {
        if (!Same(resource.GetProperty("id").GetString(), target)) throw new JsonException();
        var properties = resource.GetProperty("properties");
        if (target.Contains("/roleassignments/", StringComparison.Ordinal))
        {
            var grant = Grants(manifest, github).Single(g => g.Target == target);
            if (!Same(properties.GetProperty("scope").GetString(), grant.Scope)
                || !Same(properties.GetProperty("roleDefinitionId").GetString(), grant.RoleDefinitionId)
                || properties.GetProperty("principalId").GetString() != grant.PrincipalId
                || properties.GetProperty("principalType").GetString() != "ServicePrincipal"
                || properties.GetProperty("createdBy").GetString() != manifest.Identity.AzureActorId
                || Optional(properties, "condition") is not null || Optional(properties, "conditionVersion") is not (null or "2.0")
                || properties.TryGetProperty("updatedOn", out var updated) && updated.ValueKind != JsonValueKind.Null
                    && updated.GetDateTimeOffset() != properties.GetProperty("createdOn").GetDateTimeOffset())
                throw new JsonException("Exact unmodified source grant required.");
            return properties.GetProperty("createdOn").GetDateTimeOffset().ToString("O");
        }
        string incarnation;
        if (resource.TryGetProperty("systemData", out var system))
        {
            var created = system.GetProperty("createdAt").GetDateTimeOffset();
            if (system.TryGetProperty("lastModifiedAt", out var modified) && modified.GetDateTimeOffset() != created)
                throw new JsonException("Source resource incarnation/configuration changed.");
            incarnation = created.ToUniversalTime().ToString("O");
        }
        else if (target.Contains("/microsoft.network/", StringComparison.Ordinal)
            && resource.GetProperty("etag").GetString() is { Length: > 0 and < 2048 } etag)
            incarnation = "etag-sha256:" + TextHash(etag);
        else throw new JsonException("Native source creation incarnation unavailable.");
        if (target == ProgressTarget(github) && (properties.GetProperty("publicNetworkAccess").GetString() != "Disabled"
                || properties.GetProperty("disableLocalAuth").ValueKind != JsonValueKind.True
                || FactoryGovernance.NormalizeEndpoint(properties.GetProperty("endpoint").GetString()!) != ProgressEndpoint(github.FactoryId)
                || resource.GetProperty("sku").GetProperty("name").GetString() != "Standard"))
            throw new JsonException("Private source-only progress store required.");
        if (target.EndsWith("-source-config", StringComparison.Ordinal))
        {
            var connections = properties.GetProperty("privateLinkServiceConnections").EnumerateArray().ToArray();
            if (!Same(properties.GetProperty("subnet").GetProperty("id").GetString(), github.FactoryResourceGroupId
                + "/providers/microsoft.network/virtualnetworks/" + Prefix(github.FactoryId) + "-network/subnets/private-endpoints")
                || connections.Length != 1) throw new JsonException();
            var connection = connections[0].GetProperty("properties");
            if (!Same(connection.GetProperty("privateLinkServiceId").GetString(), ProgressTarget(github))
                || !FactoryModelsValidation.Strings(connection, "groupIds").SequenceEqual(["configurationStores"])
                || connection.GetProperty("privateLinkServiceConnectionState").GetProperty("status").GetString() != "Approved")
                throw new JsonException();
        }
        if (target.EndsWith("/privatednszonegroups/default", StringComparison.Ordinal))
        {
            var zones = properties.GetProperty("privateDnsZoneConfigs").EnumerateArray().ToArray();
            if (zones.Length != 1 || !Same(zones[0].GetProperty("properties").GetProperty("privateDnsZoneId").GetString(),
                github.FactoryResourceGroupId + "/providers/microsoft.network/privatednszones/privatelink.azconfig.io")) throw new JsonException();
        }
        return incarnation;
    }

    public static bool CreationIncarnation(FoundationOperation effect) =>
        DateTimeOffset.TryParse(effect.Incarnation, CultureInfo.InvariantCulture, out var created) && created >= effect.StartedAt
        || effect.Target.Contains("/microsoft.network/", StringComparison.Ordinal)
            && effect.Incarnation is { } incarnation && incarnation.StartsWith("etag-sha256:", StringComparison.Ordinal)
            && Hash(incarnation["etag-sha256:".Length..]);

    public static void ValidateRole(JsonElement role, ModelAccessGrant grant)
    {
        var scoped = grant.Scope + "/providers/microsoft.authorization/roledefinitions/" + grant.RoleDefinitionId.Split('/').Last();
        if (!Same(role.GetProperty("id").GetString(), grant.RoleDefinitionId) && !Same(role.GetProperty("id").GetString(), scoped)) throw new JsonException();
        var properties = role.GetProperty("properties");
        var permissions = properties.GetProperty("permissions").EnumerateArray().ToArray();
        if (properties.GetProperty("type").GetString() != "BuiltInRole" || permissions.Length != 1) throw new JsonException();
        var permission = permissions[0];
        var actions = FactoryModelsValidation.Strings(permission, "actions");
        var data = FactoryModelsValidation.Strings(permission, "dataActions");
        var reader = grant.RoleDefinitionId.EndsWith(ReaderRole, StringComparison.Ordinal);
        var writer = grant.RoleDefinitionId.EndsWith(ProgressWriterRole, StringComparison.Ordinal);
        if (FactoryModelsValidation.Strings(permission, "notActions").Length != 0
            || FactoryModelsValidation.Strings(permission, "notDataActions").Length != 0
            || reader && (!actions.SequenceEqual(["*/read"]) || data.Length != 0)
            || !reader && (actions.Length != 0 || writer && !data.SequenceEqual(["Microsoft.AppConfiguration/configurationStores/*"])
                || !writer && (data.Length == 0 || data.Any(d => !d.StartsWith("Microsoft.AppConfiguration/configurationStores/", StringComparison.Ordinal)
                    || !d.EndsWith("/read", StringComparison.Ordinal)))))
            throw new JsonException("Only scoped metadata Reader, product data reader and source-store-only data owner allowed.");
    }

    public static string NativeRevision(JsonElement resource, string target)
    {
        var properties = resource.GetProperty("properties");
        var host = target.Contains("/microsoft.app/containerapps/", StringComparison.Ordinal)
            && !target.EndsWith("/authconfigs/current", StringComparison.Ordinal);
        // Runtime status and ready-revision observations are not operator configuration.
        var observed = host ? JsonSerializer.SerializeToElement(new
        {
            id = resource.GetProperty("id"), systemData = resource.GetProperty("systemData"),
            identity = resource.GetProperty("identity"), environment = properties.GetProperty("managedEnvironmentId"),
            configuration = properties.GetProperty("configuration"), template = properties.GetProperty("template"),
        }) : resource;
        return FactoryModelsValidation.Hash(CanonicalNative(observed));
    }

    private static string CanonicalNative(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Object => "{" + string.Join(",", value.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal)
            .Select(p => JsonSerializer.Serialize(p.Name) + ":" + CanonicalNative(p.Value))) + "}",
        JsonValueKind.Array => "[" + string.Join(",", value.EnumerateArray().Select(CanonicalNative)) + "]",
        _ => value.GetRawText(),
    };

    public static bool Hash(string? value) => value is { Length: 64 } && value.All(char.IsAsciiHexDigit);
    public static bool GitId(string? value) => value is { Length: 40 } && value.All(c => char.IsAsciiHexDigit(c) && !char.IsUpper(c));
    private static bool Same(string? a, string? b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    private static string? Optional(JsonElement value, string property) =>
        value.TryGetProperty(property, out var item) && item.ValueKind != JsonValueKind.Null ? item.GetString() : null;
}
