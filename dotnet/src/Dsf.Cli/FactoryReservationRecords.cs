using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Dsf.Core.Onboarding;
using Dsf.Core.Runtime;

namespace Dsf.Cli;

internal static class FactoryReservationRecords
{
    public const string AnchorKind = "decide-reservation";
    public const string ChunkKind = "decide-plan-chunk";
    public const string ClaimKind = "decide-observation-claim";
    public const int ChunkBytes = 4000;
    public const int MaxPlanBytes = 1024 * 1024;
    public const int MaxClaims = 512;
    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectNullableAnnotations = true,
        RespectRequiredConstructorParameters = true,
        AllowDuplicateProperties = false,
        IgnoreReadOnlyProperties = true,
        Converters = { new JsonStringEnumConverter(allowIntegerValues: false) },
    };

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Json);
    public static T Parse<T>(string value) =>
        JsonSerializer.Deserialize<T>(value, Json) ?? throw new JsonException();
    public static string Hash(string value) => Hash(Encoding.UTF8.GetBytes(value));
    public static string Hash(byte[] value) => Convert.ToHexStringLower(SHA256.HashData(value));

    public static bool TryIdentify(string key, string? label, string value, out FactoryReservationIdentity? identity)
    {
        identity = null;
        if (label is not (null or "\0")) return false;
        try
        {
            using var document = JsonDocument.Parse(value);
            switch (document.RootElement.GetProperty("kind").GetString())
            {
                case AnchorKind:
                    var anchor = Parse<FactoryReservationAnchor>(value);
                    if (anchor.SchemaVersion != 1 || key != ProductConfigurationKeys.OwnerProductClaim(anchor.Identity.ProductKey)
                        || anchor.PlanBytes is <= 0 or > MaxPlanBytes
                        || anchor.PlanChunks != (anchor.PlanBytes + ChunkBytes - 1) / ChunkBytes
                        || anchor.ClaimCount is < 3 or > MaxClaims
                        || anchor.CompletedSteps < 0 || anchor.CompletedSteps > anchor.PlanChunks + anchor.ClaimCount - 1
                        || !IsHash(anchor.PlanSha256) || !Enum.IsDefined(anchor.State)
                        || anchor.State == FactoryReservationState.Reserved
                            && (anchor.CompletedSteps != anchor.PlanChunks + anchor.ClaimCount - 1 || anchor.Intent is not null)
                        || anchor.Intent is { } intent && (intent.Step != anchor.CompletedSteps
                            || intent.Step >= anchor.PlanChunks + anchor.ClaimCount - 1 || intent.Label != ProductConfigurationKeys.NoLabel
                            || intent.Key.Length > 256 || !intent.Key.StartsWith(ProductConfigurationKeys.OwnerOnboardingPrefix, StringComparison.Ordinal)
                            || !IsHash(intent.ValueSha256))
                        || anchor.Offboarding is { } offboarding
                            && (offboarding.OperationId == Guid.Empty
                                || offboarding.State is not (FactoryOffboardingState.Partial or FactoryOffboardingState.Detached)
                                || !IsHash(offboarding.PlanFingerprint)
                                || string.IsNullOrWhiteSpace(offboarding.AuthorityRevision) || offboarding.AuthorityRevision.Length > 256
                                || offboarding.RequestedAt.Offset != TimeSpan.Zero
                                || offboarding.RequestedAt < anchor.Identity.CreatedAt
                                || offboarding.State == FactoryOffboardingState.Detached != (offboarding.CompletedAt is not null)
                                || offboarding.CompletedAt is { } completed
                                    && (completed.Offset != TimeSpan.Zero || completed < offboarding.RequestedAt))
                        || anchor.Purge is { } purge
                            && (anchor.Offboarding?.State != FactoryOffboardingState.Detached
                                || purge.OperationId == Guid.Empty || purge.State != FactoryPurgeState.Purging
                                || !IsHash(purge.PlanFingerprint)
                                || string.IsNullOrWhiteSpace(purge.AuthorityRevision)
                                || string.IsNullOrWhiteSpace(purge.AuditRevision)
                                || purge.RequestedAt.Offset != TimeSpan.Zero
                                || purge.RequestedAt < anchor.Identity.CreatedAt
                                || purge.CompletedAt is not null))
                        return false;
                    identity = anchor.Identity;
                    break;
                case ChunkKind:
                    var chunk = Parse<FactoryReservationPlanChunk>(value);
                    if (chunk.SchemaVersion != 1 || chunk.Index < 0 || chunk.Index >= (MaxPlanBytes + ChunkBytes - 1) / ChunkBytes
                        || key != ProductConfigurationKeys.OwnerReservationPlanChunk(chunk.Identity.FactoryId, chunk.Index)
                        || Convert.FromBase64String(chunk.Data).Length is <= 0 or > ChunkBytes) return false;
                    identity = chunk.Identity;
                    break;
                case ClaimKind:
                    var claim = Parse<FactoryReservationClaim>(value);
                    if (claim.SchemaVersion != 1 || key != claim.Claim.Key || claim.Claim.Label != ProductConfigurationKeys.NoLabel)
                        return false;
                    var expected = claim.Claim.Kind switch
                    {
                        "repository" when claim.Claim.Target == claim.Identity.RepositoryId.ToString(System.Globalization.CultureInfo.InvariantCulture) =>
                            ProductConfigurationKeys.OwnerRepositoryClaim(claim.Identity.RepositoryId),
                        "resource" when claim.Claim.Target == BoundarySelectionInput.ResourceId(
                            claim.Claim.Target, claim.Identity.SubscriptionId, "claim.target") =>
                            ProductConfigurationKeys.OwnerResourceClaim(claim.Claim.Target),
                        _ => null,
                    };
                    if (key != expected) return false;
                    identity = claim.Identity;
                    break;
                default: return false;
            }
            new RepositoryAttachmentReviewRequest("owner/repository", identity.OwnerEndpoint, null).Validate();
            return identity.OwnerEndpoint == new Uri(identity.OwnerEndpoint).GetLeftPart(UriPartial.Authority).ToLowerInvariant()
                && identity.FactoryId != Guid.Empty && identity.OperationId != Guid.Empty && identity.RepositoryId > 0
                && identity.GitHubActorId > 0
                && identity.AzureActorId == BoundarySelectionInput.GuidValue(identity.AzureActorId, "actor")
                && identity.TenantId == BoundarySelectionInput.GuidValue(identity.TenantId, "tenant")
                && identity.SubscriptionId == BoundarySelectionInput.GuidValue(identity.SubscriptionId, "subscription")
                && identity.ProductKey == BoundarySelectionInput.Name(identity.ProductKey, "product")
                && identity.ApplicationEnvironment == ApplicationBoundaryPolicy.EnvironmentName(BoundarySelectionInput.Name(identity.ApplicationEnvironment, "environment"))
                && identity.CreatedAt != default && identity.CreatedAt.Offset == TimeSpan.Zero
                && IsHash(identity.PlanFingerprint) && IsHash(identity.SelectionFingerprint);
        }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException or FormatException
                                     or ArgumentNullException or NullReferenceException or BoundaryInputException)
        { identity = null; return false; }
    }

    private static bool IsHash(string value) => value.Length == 64 && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    public static IReadOnlyList<(string Key, string Value)> Steps(FactoryReservationAnchor anchor, byte[] plan, ApplicationBoundaryPlan reviewed)
    {
        var steps = new List<(string Key, string Value)>();
        for (var index = 0; index < anchor.PlanChunks; index++)
            steps.Add((ProductConfigurationKeys.OwnerReservationPlanChunk(anchor.Identity.FactoryId, index),
                Serialize(new FactoryReservationPlanChunk(1, ChunkKind, anchor.Identity, index,
                    Convert.ToBase64String(plan.AsSpan(index * ChunkBytes, Math.Min(ChunkBytes, plan.Length - index * ChunkBytes)))))));
        foreach (var claim in reviewed.OwnerAdmission!.ProposedClaims.Where(c => c.Kind != "product")
                     .OrderBy(c => c.Key, StringComparer.Ordinal).ThenBy(c => c.Label, StringComparer.Ordinal))
            steps.Add((claim.Key, Serialize(new FactoryReservationClaim(1, ClaimKind, anchor.Identity, claim))));
        foreach (var step in steps) OwnerReservationStore.CheckSize(step.Key, ProductConfigurationKeys.NoLabel, step.Value);
        return steps;
    }
}

// Constructed only from exact remote records verified against the immutable admitted plan.
internal sealed class ReservationAdmissionProof
{
    private readonly Dictionary<string, (string Value, string ETag)> records = [];
    public void Add(Azure.Data.AppConfiguration.ConfigurationSetting record) => records.Add(record.Key, (record.Value, record.ETag.ToString()));
    public bool Matches(string key, string? label, string value, string etag) => label is null or "\0"
        && records.TryGetValue(key, out var expected) && expected == (value, etag);
}
