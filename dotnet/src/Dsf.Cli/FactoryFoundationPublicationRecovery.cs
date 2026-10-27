using System.Net;
using System.Text;
using System.Text.Json;
using Azure.Core;
using Dsf.Core.Onboarding;
using Dsf.Core.Runtime;

namespace Dsf.Cli;

internal sealed partial class FactoryFoundationService
{
    private async Task<FactoryReservationResult> RecoverPublicationAsync(
        FactoryReservationResult admission, FactoryFoundationPointer pointer, FactoryReservationResult inspected, CancellationToken token)
    {
        var identity = admission.Identity!;
        var store = Store(identity);
        try
        {
            var (root, anchor, operations) = await ReadRecoveryJournalAsync(admission, pointer, inspected, store, token);
            if (operations.Count < 2) return inspected;
            var configuration = operations[1].Operation;
            var rejected = operations.Count > 2 && operations[^1].Operation is
                { State: FoundationOperationState.Failed, Disposition: FoundationResourceDisposition.NoEffect } failed ? failed : null;
            var completedPublications = operations.Count - 2 - (rejected is null ? 0 : 1);
            if (operations[0].Operation.State != FoundationOperationState.Completed
                || configuration.State is FoundationOperationState.Intent or FoundationOperationState.Failed or FoundationOperationState.Canceled
                || configuration.HttpStatus != 201 || configuration.Incarnation is null
                || configuration.ProviderRequestId is null || configuration.ResponseDate is null
                || operations.Skip(2).Take(completedPublications).Any(o => o.Operation.State != FoundationOperationState.Completed)
                || rejected is null && operations.Skip(2).Any(o => o.Operation.State != FoundationOperationState.Completed))
                return inspected;
            var assets = new FactoryFoundationAssets(identity, admission.Plan!.Selection);
            VerifyInitialRequestRevision(operations.Select(o => o.Operation).ToArray(), assets);
            var endpoint = "https://dsfo-" + identity.FactoryId.ToString("N") + ".azconfig.io";
            if (configuration.Target != assets.Group + "/providers/microsoft.appconfiguration/configurationstores/dsfo-" + identity.FactoryId.ToString("N")
                || configuration.ApiVersion != "2024-06-01" || configuration.Endpoint != endpoint
                || pointer.ProductEndpoint is not null && pointer.ProductEndpoint != endpoint)
                throw new JsonException();
            var access = await new OwnerReservationStore.OperatorCredential(azure, identity.TenantId, identity.SubscriptionId,
                identity.AzureActorId, "https://management.azure.com/")
                .GetTokenAsync(new TokenRequestContext(["https://management.azure.com/.default"]), token);
            using var response = await ReadArmAsync(configuration.Target, configuration.ApiVersion, access.Token, token);
            if (!response.IsSuccessStatusCode) throw new JsonException();
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
            var current = ReadCreationReceipt(configuration, document.RootElement, HttpStatusCode.Created);
            VerifyConfigurationPolicy(document.RootElement, admission.Plan.Selection.FactoryLocation);
            if (current.Incarnation != configuration.Incarnation || current.Endpoint != endpoint)
                throw new ReservationStoreException(FactoryReservationState.Blocked,
                    "Product App Configuration incarnation changed; no adoption of a recreated resource ID or publication.");
            if (current.State != FoundationOperationState.Completed) return inspected;
            RequireUnchangedReceipt(configuration, current);
            var profile = FactoryReservationRecords.Serialize(new FactoryOnboardingProfile(
                1, "onboard-decide-inactive-v1", identity.FactoryId, identity.PlanFingerprint, []));
            var pin = FactoryReservationRecords.Hash(profile);
            if (pointer.ProfilePin is not null && pointer.ProfilePin != pin) throw new JsonException();
            var expected = ExpectedPublications(admission, anchor, profile, pin);
            if (operations.Count - 2 > expected.Count) throw new JsonException();
            var product = new OwnerReservationStore(endpoint, identity.TenantId, identity.SubscriptionId, azure, http, identity.AzureActorId);
            var existing = await product.ReadNamespaceAsync("dsf/factory/", token);
            if (existing.Count != completedPublications)
                throw new ReservationStoreException(FactoryReservationState.Unknown,
                    "Publication namespace has missing/unreceipted or newer records; matching bytes cannot replace lost original ETags. No replay; administrator handoff required.");
            for (var i = 2; i < operations.Count; i++)
            {
                var step = operations[i].Operation;
                var (key, value) = expected[i - 2];
                if (step.Target != endpoint + "/kv/" + Uri.EscapeDataString(key) || step.ApiVersion != "1.0"
                    || step.RequestSha256 != FactoryReservationRecords.Hash(value)
                    || step == rejected && existing.ContainsKey(key)
                    || step != rejected && (!existing.TryGetValue(key, out var record) || record.Value != value || record.ETag.ToString() != step.Incarnation))
                    throw new ReservationStoreException(FactoryReservationState.Blocked,
                        "Original publication input/revision or current key incarnation differs; newer authority is never overwritten from the owner archive.");
            }
            if (pointer.Stage == "product-authoritative")
            {
                if (pointer.State != FactoryFoundationState.Blocked || pointer.RecoveryDetail != GroupRecoveryHandoff)
                    await TakeRecoveryCoordinatorAsync(store, root, anchor with { Foundation = pointer with
                        { State = FactoryFoundationState.Blocked, RecoveryDetail = GroupRecoveryHandoff } }, token);
                return RecoveryFailureWithProgress(admission, inspected, FactoryReservationState.Blocked, GroupRecoveryHandoff);
            }
            anchor = anchor with { Foundation = pointer with
                { Stage = "product-authority-pending", ProductEndpoint = endpoint, ProfilePin = pin, State = FactoryFoundationState.Partial } };
            (root, anchor) = await TakeRecoveryCoordinatorAsync(store, root, anchor, token);
            await SaveReconciledReceiptAsync(store, operations[1].Record, configuration, current, token);
            confirmedOperations[current.Index] = current;
            if (rejected is not null)
            {
                var (key, value) = expected[completedPublications];
                (root, anchor) = await PublishAsync(store, product, root, anchor, key, value, token, rejected);
            }
            foreach (var (key, value) in expected.Skip(operations.Count - 2))
                (root, anchor) = await PublishAsync(store, product, root, anchor, key, value, token);
            anchor = anchor with { Foundation = anchor.Foundation! with
                { Stage = "product-authoritative", State = FactoryFoundationState.Blocked, RecoveryDetail = GroupRecoveryHandoff } };
            root = await SaveRootAsync(store, root, anchor, token);
            var completed = await InspectAsync(admission, anchor.Foundation!, token);
            return RecoveryFailure(completed, FactoryReservationState.Blocked, GroupRecoveryHandoff);
        }
        catch (ReservationStoreException e)
        { return RecoveryFailureWithProgress(admission, inspected, e.State, e.Message); }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        { return RecoveryFailureWithProgress(admission, inspected, FactoryReservationState.Canceled, "Canceled during recovery; dispatched effects remain held. No rollback or cleanup."); }
        catch (Exception e) when (e is JsonException or InvalidOperationException or FormatException or KeyNotFoundException
            or HttpRequestException or OperationCanceledException or OwnerReservationStore.AuthenticationFailedException)
        { return RecoveryFailureWithProgress(admission, inspected, FactoryReservationState.Unknown, "Publication authority/input/provider revision is unproven; no further dispatch. Retain the original operation for administrator reconciliation."); }
    }

    private static IReadOnlyList<(string Key, string Value)> ExpectedPublications(
        FactoryReservationResult admission, FactoryReservationAnchor anchor, string profile, string pin)
    {
        var bytes = Encoding.UTF8.GetBytes(BoundaryPlanFingerprint.Canonical(JsonSerializer.SerializeToElement(admission.Plan, FactoryReservationRecords.Json)));
        if (bytes.Length != anchor.PlanBytes || FactoryReservationRecords.Hash(bytes) != anchor.PlanSha256)
            throw new JsonException();
        List<(string Key, string Value)> expected = [(ProductConfigurationKeys.FactoryProfile, profile)];
        var original = FactoryReservationRecords.Steps(anchor, bytes, admission.Plan!);
        for (var i = 0; i < anchor.PlanChunks; i++)
            expected.Add((ProductConfigurationKeys.FactoryPlanChunk(i), original[i].Value));
        expected.Add((ProductConfigurationKeys.FactoryManifest, FactoryReservationRecords.Serialize(new FactoryManifest(
            1, "decide-factory-manifest", "product-app-configuration", "Inactive",
            anchor.Identity, anchor.PlanSha256, anchor.PlanBytes, anchor.PlanChunks, pin))));
        return expected;
    }

    private static void VerifyInitialRequestRevision(IReadOnlyList<FoundationOperation> operations, FactoryFoundationAssets assets)
    {
        if (operations.Count < 2 || operations[0].Target != assets.Group || operations[0].ApiVersion != "2021-04-01"
            || operations[0].RequestSha256 != FactoryReservationRecords.Hash(assets.GroupRequest())
            || operations[1].Target != assets.Configuration || operations[1].ApiVersion != "2024-06-01"
            || operations[1].RequestSha256 != FactoryReservationRecords.Hash(assets.ConfigurationRequest()))
            throw new ReservationStoreException(FactoryReservationState.Blocked,
                "Original resource request inputs/revision do not match the admitted plan and installed implementation; no reuse or dispatch.");
    }

    private static void VerifyConfigurationPolicy(JsonElement resource, string location)
    {
        var properties = resource.GetProperty("properties");
        if (resource.GetProperty("location").GetString() != location
            || !string.Equals(resource.GetProperty("sku").GetProperty("name").GetString(), "standard", StringComparison.OrdinalIgnoreCase)
            || !properties.GetProperty("disableLocalAuth").GetBoolean() || !properties.GetProperty("enablePurgeProtection").GetBoolean()
            || properties.GetProperty("softDeleteRetentionInDays").GetInt32() != 7
            || properties.GetProperty("publicNetworkAccess").GetString() != "Enabled")
            throw new ReservationStoreException(FactoryReservationState.Blocked,
                "Current product configuration policy differs from the original request; recovery does not overwrite newer native state.");
    }
}
