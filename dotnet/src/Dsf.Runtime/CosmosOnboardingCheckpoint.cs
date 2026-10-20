using System.Text.Json;
using Dsf.Core.Onboarding;
using Dsf.FeatureCouncil.Conveyor;

namespace Dsf.Runtime;

internal sealed partial class CosmosRunStore
{
    private readonly HashSet<string> unconfirmedCheckpoints = new(StringComparer.Ordinal);
    internal string? CheckpointETag(string id) => checkpointVersions.GetValueOrDefault(id);

    private async Task SaveOnboardingCheckpointAsync(string id, string document, CancellationToken token)
    {
        if (unconfirmedCheckpoints.Contains(id)) throw new CosmosCheckpointException(CosmosCheckpointFailure.Unknown);
        try
        {
            var receipt = await gateway.WriteCheckpointAsync(endpoint, database, container, product, id,
                document, checkpointVersions.GetValueOrDefault(id), token);
            checkpointVersions[id] = receipt.ETag;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            unconfirmedCheckpoints.Add(id);
            throw;
        }
        catch (CosmosCheckpointException)
        {
            unconfirmedCheckpoints.Add(id);
            throw;
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or OperationCanceledException
            or InvalidOperationException or NotSupportedException or ArgumentException)
        {
            unconfirmedCheckpoints.Add(id);
            throw new CosmosCheckpointException(CosmosCheckpointFailure.Unknown);
        }
    }

    private async Task<ConveyorRun?> LoadOnboardingCheckpointAsync(string id, CancellationToken token)
    {
        try
        {
            var checkpoint = await gateway.ReadCheckpointAsync(endpoint, database, container, product, id, token);
            if (checkpoint is null)
            {
                if (checkpointVersions.ContainsKey(id)) throw new CosmosCheckpointException(CosmosCheckpointFailure.Unknown);
                return null;
            }
            using var document = JsonDocument.Parse(checkpoint.Json, new JsonDocumentOptions { AllowDuplicateProperties = false });
            var root = document.RootElement;
            if (root.GetProperty("id").GetString() != id || root.GetProperty("product").GetString() != product
                || root.GetProperty("onboardingProtocol").GetInt32() != 1)
                throw new JsonException();
            foreach (var required in new[] { "profile", "evaluationContext", "evaluationRevision", "synthesisEvaluations",
                "modelObservations", "proposals", "evidence", "checkpoints", "previewedIssues", "filedIssues", "audit" })
                _ = root.GetProperty(required);
            var run = Deserialize(checkpoint.Json);
            if (run.Profile != ConveyorProfile.OnboardingPreview
                || root.GetProperty("onboardingChecksum").GetString() != FactoryModelsValidation.Hash(run))
                throw new JsonException();
            checkpointVersions[id] = checkpoint.ETag;
            return run;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            unconfirmedCheckpoints.Add(id);
            throw;
        }
        catch (CosmosCheckpointException)
        {
            unconfirmedCheckpoints.Add(id);
            throw;
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or OperationCanceledException
            or InvalidOperationException or NotSupportedException or ArgumentException or JsonException or KeyNotFoundException or FormatException)
        {
            unconfirmedCheckpoints.Add(id);
            throw new CosmosCheckpointException(CosmosCheckpointFailure.Unknown);
        }
    }
}
