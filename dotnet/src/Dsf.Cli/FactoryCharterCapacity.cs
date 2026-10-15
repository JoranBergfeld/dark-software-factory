using Dsf.Core.Onboarding;
using Dsf.Core.Runtime;

namespace Dsf.Cli;

internal sealed partial class FactoryCharterService
{
    private const int MaximumReconciliationRevisionCharacters = 256;

    private static void CheckPublicationCapacity(CharterPublication publication, CharterReview review)
    {
        var effect = publication.Effects.Last();
        var receipt = new CharterNativeReceipt("POST", "graphql", 200, new string('x', 128), new string('a', 64));
        var response = new CharterResponseRecord(1, publication.FactoryId, review.RepositoryId, publication.Operation, effect.Attempt,
            review.Fingerprint, effect.RequestSha256, receipt, new string('x', FactoryCharterResponses.MaximumEncodedCharacters));
        var positive = effect with
        {
            State = CharterEffectState.Confirmed, Receipt = receipt,
            ObjectId = effect.Index == 1 ? new string('a', 40) : new string('\u0001', 128),
            ResponseRecord = new(new string('a', 64), new string('a', 64)),
            Reconciliation = new(new string('\u0001', MaximumReconciliationRevisionCharacters),
                Guid.Empty.ToString(), DateTimeOffset.MaxValue),
        };
        var completed = WithEffect(publication, positive) with
        {
            State = effect.Index == 2 ? FactoryCharterState.Published : FactoryCharterState.Partial,
            PullRequest = effect.Index == 2 ? new(null, positive.ObjectId!, int.MaxValue,
                $"https://github.com/{review.Repository}/pull/{int.MaxValue}", review.RepositoryId,
                review.DefaultBranch, review.Commit, publication.Branch["refs/heads/".Length..], new string('a', 40)) : publication.PullRequest,
        };
        var rejected = WithEffect(publication, effect with
        {
            State = CharterEffectState.NotDispatched, Receipt = receipt with { Status = int.MaxValue },
        }) with { State = FactoryCharterState.Unknown };
        try
        {
            // Reserve this issued step's entire lifecycle, retaining exact prior attempts and legacy witnesses.
            // Later steps and retries independently reserve before dispatch; remote effects are never undone.
            foreach (var value in new[] { publication, completed, rejected })
            {
                CheckRoot(PublicationKey, value);
                CheckRoot(PublicationHistoryKey(publication.Sequence), value);
            }
            OwnerReservationStore.CheckSize(FactoryCharterResponses.Key(publication.Operation, effect.Attempt),
                ProductConfigurationKeys.NoLabel, FactoryReservationRecords.Serialize(response));
        }
        catch (ReservationStoreException)
        {
            throw new CharterOperationException(FactoryCharterState.Partial,
                "Complete original response, prior attempts and reconciliation exceed the reserved 10 KB lifecycle; no next GitHub dispatch.");
        }
    }

    private static void CheckPublicationPayloadCapacity(CharterPublication publication, IReadOnlyList<string> parts)
    {
        for (var i = 0; i < parts.Count; i++)
            OwnerReservationStore.CheckSize(PayloadKey(publication.Operation, i), ProductConfigurationKeys.NoLabel,
                FactoryReservationRecords.Serialize(parts[i]));
        CheckRoot(PublicationKey, publication);
        CheckRoot(PublicationInitializedKey, publication);
    }

    private static void CheckRoot(string key, CharterPublication value) =>
        OwnerReservationStore.CheckSize(key, ProductConfigurationKeys.NoLabel, FactoryReservationRecords.Serialize(value));
}
