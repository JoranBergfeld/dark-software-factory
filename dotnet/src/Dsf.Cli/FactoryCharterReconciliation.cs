using System.Text.Json;
using Dsf.Core.Onboarding;

namespace Dsf.Cli;

internal sealed partial class FactoryCharterService
{
    private const int MaximumCharterAttempts = 8;
    private const int MaximumCharterResponseBytes = FactoryCharterResponses.MaximumBytes;

    internal async Task<FactoryCharterResult> InspectPublicationAsync(FactoryReservationResult admission, CancellationToken token)
    {
        var store = ProductStore(admission);
        var root = await store.ReadAsync(PublicationKey, token);
        if (root is null)
        {
            await store.RequirePristineAsync(PublicationPrefix, token);
            return new(FactoryCharterState.Blocked, "No original charter publication. Inspection creates no records.");
        }
        var publication = ReadPublication(root, admission);
        var payload = await ReadPayloadAsync(store, publication, token);
        if (payload.Review.RepositoryId != admission.Identity!.RepositoryId) throw new JsonException();
        return Result(publication, root, "Original historical publication; no current readiness, adoption or activation asserted.",
            PublicationState(publication)) with { Review = payload.Review, NextAction = NextPublicationAction(publication) };
    }

    internal async Task<FactoryCharterResult> ReconcilePublicationAsync(FactoryReservationResult admission, Guid operation,
        string revision, CancellationToken token)
    {
        var store = ProductStore(admission);
        var root = await store.ReadAsync(PublicationKey, token) ?? throw new JsonException();
        var publication = ReadPublication(root, admission);
        var payload = await ReadPayloadAsync(store, publication, token);
        if (payload.Review.RepositoryId != admission.Identity!.RepositoryId) throw new JsonException();
        RequireOriginalPublication(publication, root.ETag.ToString(), operation, revision);
        if (publication.Effects.LastOrDefault() is not { State: CharterEffectState.Dispatched or CharterEffectState.Unknown } effect)
            return Result(publication, root, "No unresolved issued effect; no GitHub mutation.", PublicationState(publication))
                with { Review = payload.Review };
        var witness = await ReadResponseAsync(store, publication, effect, token);
        if (witness is null)
            return Result(publication, root, "Original native response unavailable. Search matches, shared authors and copied markers prove neither creation nor absence.",
                FactoryCharterState.Unknown) with { Review = payload.Review, NextAction = NextPublicationAction(publication) };
        if (revision.Length > MaximumReconciliationRevisionCharacters)
            throw new CharterOperationException(FactoryCharterState.Partial, "Original native revision exceeds the bounded reconciliation metadata.");
        var confirmed = ConfirmResponse(publication, payload.Review, effect with { Response = witness });
        confirmed = WithEffect(confirmed, confirmed.Effects.Last() with
        {
            Reconciliation = new(revision, admission.Identity!.AzureActorId, clock.GetUtcNow()),
        });
        var saved = await SavePublicationAsync(store, root, confirmed, token);
        return Result(confirmed, saved, "Original dispatch-owned native response reconciled conditionally. No GitHub request, new intent or activation.",
            PublicationState(confirmed)) with { Review = payload.Review, NextAction = NextPublicationAction(confirmed) };
    }

    private static void RequireOriginalPublication(CharterPublication publication, string current, Guid operation, string revision)
    {
        if (operation == Guid.Empty || operation != publication.Operation || current != revision)
            throw new CharterOperationException(FactoryCharterState.Blocked, "Original operation and exact current publication revision required; stale operator cannot overwrite outcome.");
    }

    private static FactoryCharterState PublicationState(CharterPublication publication) =>
        publication.Effects.Any(e => e.State is CharterEffectState.Dispatched or CharterEffectState.Unknown) ? FactoryCharterState.Unknown
        : publication.Effects.Any(e => e.State == CharterEffectState.KnownFailed) ? FactoryCharterState.Failed
        : publication.Effects.Count == 3 && publication.Effects.All(e => e.State == CharterEffectState.Confirmed) ? FactoryCharterState.Published
        : FactoryCharterState.Partial;

    private static string NextPublicationAction(CharterPublication publication) => PublicationState(publication) switch
    {
        FactoryCharterState.Unknown => "Operator reconciles only the original retained native response; unavailable proof remains quarantined. No replacement or forced retry.",
        FactoryCharterState.Published => "Human reviews/merges the exact PR, then operator previews adoption; historical outcome does not grant current authority.",
        _ => "Operator reviews fresh exact input and open admission before explicitly continuing or retrying this original operation.",
    };

    private static CharterPublication ConfirmResponse(CharterPublication publication, CharterReview review, CharterEffect effect)
    {
        var witness = effect.Response ?? throw new JsonException();
        if (effect.Attempt == Guid.Empty || witness.FactoryId != publication.FactoryId || witness.RepositoryId != review.RepositoryId
            || witness.Operation != publication.Operation || witness.Attempt != effect.Attempt
            || witness.ReviewFingerprint != review.Fingerprint || witness.RequestSha256 != effect.RequestSha256
            || witness.Native is not { Method: "POST", Route: "graphql", Status: 200, RequestId.Length: > 0 and <= 128 }
            || witness.Native.RequestId.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-' && c != ':')
            || System.Text.Encoding.UTF8.GetByteCount(witness.ResponseJson) is not (> 0 and <= MaximumCharterResponseBytes)
            || FactoryModelsValidation.Hash(witness.ResponseJson) != witness.Native.ResponseSha256)
            throw new JsonException("Original dispatch-owned response binding differs.");
        using var body = JsonDocument.Parse(witness.ResponseJson, new() { AllowDuplicateProperties = false });
        if (body.RootElement.TryGetProperty("errors", out _)) throw new JsonException("GraphQL errors are not positive creation proof.");
        var result = body.RootElement.GetProperty("data").GetProperty(effect.Kind);
        var objectId = Confirm(effect.Index, review, publication, result);
        var confirmed = WithEffect(publication, effect with { State = CharterEffectState.Confirmed, ObjectId = objectId, Receipt = witness.Native,
            Response = effect.ResponseRecord is null ? witness : null });
        return effect.Index == 2
            ? confirmed with { PullRequest = PullReceipt(review, publication, result.GetProperty("pullRequest")), State = FactoryCharterState.Published }
            : confirmed with { State = FactoryCharterState.Partial };
    }

    private static CharterPublication PublicPublication(CharterPublication publication) =>
        publication with { Effects = publication.Effects.Select(PublicEffect).ToArray() };

    private static CharterEffect PublicEffect(CharterEffect effect)
    {
        if (effect.Response is not { } witness) return effect;
        using var response = JsonDocument.Parse(witness.ResponseJson);
        var data = response.RootElement.GetProperty("data").GetProperty(effect.Kind);
        var objectId = effect.Index switch
        {
            0 => data.GetProperty("ref").GetProperty("id").GetString(),
            1 => data.GetProperty("commit").GetProperty("oid").GetString(),
            2 => data.GetProperty("pullRequest").GetProperty("id").GetString(),
            _ => throw new JsonException(),
        };
        return effect with { Response = null, Receipt = effect.Receipt ?? witness.Native, ObjectId = effect.ObjectId ?? objectId };
    }
}
