using System.Text;
using System.Text.Json;
using Dsf.Core.Onboarding;
using Dsf.Core.Runtime;

namespace Dsf.Cli;

internal sealed class FactoryManifestStore(OwnerReservationStore store)
{
    public async Task<ApplicationBoundaryPlan> ReadReviewedPlanAsync(
        FactoryReservationIdentity identity, FactoryFoundationPointer pointer, string admittedPlanSha256, CancellationToken token)
    {
        var manifestValue = await RequiredAsync(ProductConfigurationKeys.FactoryManifest, token);
        var profileValue = await RequiredAsync(ProductConfigurationKeys.FactoryProfile, token);
        var manifest = FactoryGovernance.ReadInactiveManifest(
            manifestValue, profileValue, identity.ProductKey, pointer.ProductEndpoint ?? throw new JsonException(),
            pointer.ProfilePin ?? throw new JsonException());
        if (manifest.Identity != identity
            || manifest.ReviewedPlanSha256 != admittedPlanSha256
            || manifest.PlanBytes is <= 0 or > FactoryReservationRecords.MaxPlanBytes
            || manifest.PlanChunks != (manifest.PlanBytes + FactoryReservationRecords.ChunkBytes - 1) / FactoryReservationRecords.ChunkBytes)
            throw new JsonException();
        using var stream = new MemoryStream();
        for (var i = 0; i < manifest.PlanChunks; i++)
        {
            var chunk = FactoryReservationRecords.Parse<FactoryReservationPlanChunk>(
                await RequiredAsync(ProductConfigurationKeys.FactoryPlanChunk(i), token));
            if (chunk.SchemaVersion != 1 || chunk.Kind != FactoryReservationRecords.ChunkKind || chunk.Identity != identity || chunk.Index != i)
                throw new JsonException();
            var bytes = Convert.FromBase64String(chunk.Data);
            if (bytes.Length != Math.Min(FactoryReservationRecords.ChunkBytes, manifest.PlanBytes - i * FactoryReservationRecords.ChunkBytes))
                throw new JsonException();
            stream.Write(bytes);
        }
        var content = stream.ToArray();
        if (content.Length != manifest.PlanBytes || FactoryReservationRecords.Hash(content) != manifest.ReviewedPlanSha256)
            throw new JsonException();
        var plan = FactoryReservationRecords.Parse<ApplicationBoundaryPlan>(Encoding.UTF8.GetString(content));
        if (plan.State != BoundaryPlanState.Reviewed || plan.Fingerprint != identity.PlanFingerprint
            || plan.SelectionFingerprint != identity.SelectionFingerprint || plan.RepositoryReview.Repository?.Id != identity.RepositoryId)
            throw new JsonException();
        return plan;
    }

    private async Task<string> RequiredAsync(string key, CancellationToken token) =>
        (await store.ReadAsync(key, token))?.Value
        ?? throw new ReservationStoreException(FactoryReservationState.Unknown, "Required product manifest/profile data missing; no owner-archive fallback.");
}
