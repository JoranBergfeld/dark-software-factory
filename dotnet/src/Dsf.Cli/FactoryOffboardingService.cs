using System.Text.Json;
using System.Net;
using System.Net.Http.Headers;
using Azure.Core;
using Azure.Data.AppConfiguration;
using Dsf.Core.Onboarding;
using Dsf.Core.Runtime;

namespace Dsf.Cli;

internal sealed class FactoryOffboardingService(IAzureCliRunner azure, HttpClient? http, TimeProvider clock)
{
    private static readonly HttpClient ProviderHttp = new(new HttpClientHandler { AllowAutoRedirect = false });

    internal async Task<FactoryOffboardingResult> ExecuteAsync(
        FactoryReservationResult admission, Guid operation, string? approval, CancellationToken token)
    {
        if (admission.Identity is not { } identity || admission.Revision is null)
            return new(FactoryOffboardingState.Blocked,
                "Trusted owner recovery identity and current reservation revision required; selected IDs, names, tags or groups are not ownership.");

        var owner = new OwnerReservationStore(identity.OwnerEndpoint, identity.TenantId, identity.SubscriptionId,
            azure, http, identity.AzureActorId);
        var ownerRecord = await owner.ReadAsync(ProductConfigurationKeys.OwnerProductClaim(identity.ProductKey), token);
        if (ownerRecord is null || ownerRecord.ETag.ToString() != admission.Revision)
            return new(FactoryOffboardingState.Blocked, "Owner reservation changed before detach review; inspect current trusted recovery state.");
        var anchor = FactoryReservationRecords.Parse<FactoryReservationAnchor>(ownerRecord.Value);
        if (anchor.Identity != identity) throw new JsonException();

        OwnerReservationStore? product = null;
        IReadOnlyDictionary<string, ConfigurationSetting> authority = new Dictionary<string, ConfigurationSetting>();
        if (admission.Foundation?.ProductEndpoint is { } endpoint)
        {
            product = new OwnerReservationStore(endpoint, identity.TenantId, identity.SubscriptionId, azure, http, identity.AzureActorId);
            authority = await product.ReadFactoryAuthorityAsync(token);
        }

        var existing = await ExistingAsync(owner, product, identity, anchor, admission, authority, token);
        if (existing is not null)
        {
            if (existing.Request.RequestedAt > clock.GetUtcNow()) throw new JsonException();
            if (existing.Request.OperationId != operation)
                return Result(FactoryOffboardingState.Blocked, "Another protected detach operation already closes admission; no replacement.",
                    existing.Plan, existing.Request, true, anchor.Offboarding is not null);
            if (approval is not null && approval != existing.Plan.Fingerprint)
                return Result(FactoryOffboardingState.Blocked, "Approval differs from the retained detach plan; no cleanup replay.",
                    existing.Plan, existing.Request, true, anchor.Offboarding is not null);
            return await CompleteAsync(owner, ownerRecord, anchor, product, authority, existing.Request, existing.Plan, token);
        }

        var plan = BuildPlan(identity, operation, ownerRecord.ETag.ToString(), admission, authority);
        if (string.IsNullOrWhiteSpace(approval))
            return Result(FactoryOffboardingState.ReviewRequired,
                "Scoped detach review required; no admission or cleanup effect dispatched.", plan, null, false, false);
        if (approval != plan.Fingerprint)
            return Result(FactoryOffboardingState.Blocked, "Detach approval differs from the exact current plan; no effect dispatched.",
                plan, null, false, false);
        if (plan.OwnershipBlockers.Count != 0)
            return Result(FactoryOffboardingState.Blocked,
                "Unknown, changed or unsupported ownership blocks affected cleanup; no completed detach claimed.",
                plan, null, false, false) with { BlockedRemovals = plan.OwnershipBlockers };

        var request = new FactoryOffboardingRequest(1, FactoryOffboarding.Kind, operation, identity.FactoryId,
            plan.Fingerprint, plan.AuthorityRevision, identity.AzureActorId, clock.GetUtcNow());
        var value = JsonSerializer.Serialize(request, FactoryGitHubJson.Options);
        _ = FactoryOffboarding.Read(value, identity);

        if (product is not null)
        {
            try
            {
                await product.WriteAsync(FactoryOffboarding.Key, value, null, token);
                await product.WriteAsync(FactoryOffboarding.HistoryKey(operation), value, null, token);
            }
            catch (ReservationStoreException)
            {
                return Result(FactoryOffboardingState.Unknown,
                    "Detach admission-close response or audit copy unconfirmed; inspect retained operation, never recreate it.",
                    plan, request, true, false);
            }
        }

        return await CompleteAsync(owner, ownerRecord, anchor, product, authority, request, plan, token);
    }

    private async Task<FactoryOffboardingResult> CompleteAsync(
        OwnerReservationStore owner, ConfigurationSetting ownerRecord, FactoryReservationAnchor anchor,
        OwnerReservationStore? product, IReadOnlyDictionary<string, ConfigurationSetting> authority,
        FactoryOffboardingRequest request, FactoryOffboardingPlan plan, CancellationToken token)
    {
        var removals = new List<string>();
        var blocked = new List<string>();
        if (product is not null)
        {
            var inventory = await new FactoryPauseService(azure, http, clock)
                .InspectIssuedPublicationsAsync(new(FactoryReservationState.Reserved, "", anchor.Identity)
                {
                    Foundation = new(FactoryFoundationState.Inactive, "", "", Guid.Empty, [])
                    {
                        ProductEndpoint = anchor.Foundation!.ProductEndpoint,
                    },
                }, token);
            if (!inventory.PublicationInventoryComplete
                || inventory.IssuedPublications.Any(effect => effect.State is "Unknown" or "Dispatched"))
                blocked.Add("issued GitHub publication outcomes remain unresolved");

            var cleanup = await CleanupOwnedAccessAsync(anchor.Identity, authority, token);
            removals.AddRange(cleanup.Completed);
            blocked.AddRange(cleanup.Blocked);
        }

        var current = FactoryReservationRecords.Parse<FactoryReservationAnchor>(ownerRecord.Value);
        if (current.Offboarding is null)
        {
            var state = product is null && blocked.Count == 0
                ? FactoryOffboardingState.Detached : FactoryOffboardingState.Partial;
            current = current with
            {
                Offboarding = new(request.OperationId, state, plan.Fingerprint, plan.AuthorityRevision, request.RequestedAt,
                    state == FactoryOffboardingState.Detached ? clock.GetUtcNow() : null),
            };
            try
            {
                ownerRecord = await owner.WriteAsync(ownerRecord.Key, FactoryReservationRecords.Serialize(current),
                    ownerRecord.ETag, token);
                removals.Add("live owner registration replaced by retained detached registration");
            }
            catch (ReservationStoreException)
            {
                return Result(FactoryOffboardingState.Unknown,
                    "Admission is closed but retained owner registration update is unconfirmed; inspect exact records.",
                    plan, request, product is not null, false);
            }
        }
        else if (current.Offboarding.OperationId != request.OperationId
                 || current.Offboarding.PlanFingerprint != plan.Fingerprint)
            return Result(FactoryOffboardingState.Blocked,
                "Owner registration contains a different detach operation; no takeover or repair.",
                plan, request, product is not null, true);

        if (blocked.Count != 0)
            return Result(FactoryOffboardingState.Partial,
                "Admission closed and safe independent registry cleanup retained; unresolved issued effects prevent completed detachment.",
                plan, request, true, true) with { CompletedRemovals = removals, BlockedRemovals = blocked };

        try
        {
            var claims = await owner.ReadNamespaceAsync(ProductConfigurationKeys.OwnerOnboardingPrefix, token);
            foreach (var claim in claims.Values.Where(record =>
                         (record.Key.StartsWith(ProductConfigurationKeys.OwnerOnboardingPrefix + "repositories/", StringComparison.Ordinal)
                          || record.Key.StartsWith(ProductConfigurationKeys.OwnerOnboardingPrefix + "resources/", StringComparison.Ordinal))
                         && FactoryReservationRecords.TryIdentify(record.Key, record.Label, record.Value, out var ownerIdentity)
                         && ownerIdentity == anchor.Identity))
            {
                await owner.DeleteOnboardingClaimAsync(claim, token);
                removals.Add("Released exact eligible owner registry claim " + claim.Key + ".");
            }
        }
        catch (ReservationStoreException e)
        {
            blocked.Add(e.Message);
            return Result(e.State == FactoryReservationState.Blocked
                    ? FactoryOffboardingState.Partial : FactoryOffboardingState.Unknown,
                "Admission and workload cleanup completed, but owner claim release remains incomplete.",
                plan, request, true, true) with { CompletedRemovals = removals, BlockedRemovals = blocked };
        }

        if (product is not null)
        {
            var root = await product.ReadAsync(FactoryOffboarding.Key, token);
            var history = await product.ReadAsync(FactoryOffboarding.HistoryKey(request.OperationId), token);
            var retained = root is null ? null : FactoryOffboarding.Read(root.Value, anchor.Identity);
            if (root is null || history is null || root.Value != history.Value || retained is null
                || retained.OperationId != request.OperationId
                || retained.PlanFingerprint != plan.Fingerprint
                || retained.ApprovedBy != request.ApprovedBy
                || retained.RequestedAt != request.RequestedAt)
                return Result(FactoryOffboardingState.Unknown,
                    "Product detach/audit readback is incomplete; runtime remains closed but stop completion is unconfirmed.",
                    plan, request, true, true);
        }

        if (current.Offboarding.State != FactoryOffboardingState.Detached)
        {
            current = current with
            {
                Offboarding = current.Offboarding with
                {
                    State = FactoryOffboardingState.Detached,
                    CompletedAt = clock.GetUtcNow(),
                },
            };
            try
            {
                await owner.WriteAsync(ownerRecord.Key, FactoryReservationRecords.Serialize(current),
                    ownerRecord.ETag, token);
            }
            catch (ReservationStoreException)
            {
                return Result(FactoryOffboardingState.Unknown,
                    "Stop and product audit are confirmed but final retained owner registration update is unconfirmed.",
                    plan, request, true, true) with { CompletedRemovals = removals };
            }
        }

        return Result(FactoryOffboardingState.Detached, FactoryOffboarding.RedactedDetail,
            plan, request, true, true) with { CompletedRemovals = removals };
    }

    private sealed record ExistingOffboarding(FactoryOffboardingRequest Request, FactoryOffboardingPlan Plan);

    private static Task<ExistingOffboarding?> ExistingAsync(
        OwnerReservationStore owner, OwnerReservationStore? product, FactoryReservationIdentity identity,
        FactoryReservationAnchor anchor, FactoryReservationResult admission,
        IReadOnlyDictionary<string, ConfigurationSetting> authority, CancellationToken token)
    {
        if (product is not null && authority.TryGetValue(FactoryOffboarding.Key, out var root))
        {
            var retainedRequest = FactoryOffboarding.Read(root.Value, identity);
            var retainedPlan = BuildPlan(identity, retainedRequest.OperationId, retainedRequest.AuthorityRevision, admission, authority);
            if (retainedPlan.Fingerprint != retainedRequest.PlanFingerprint) throw new JsonException();
            return Task.FromResult<ExistingOffboarding?>(new(retainedRequest, retainedPlan));
        }
        if (anchor.Offboarding is null) return Task.FromResult<ExistingOffboarding?>(null);
        var plan = BuildPlan(identity, anchor.Offboarding.OperationId, anchor.Offboarding.AuthorityRevision,
            admission, authority);
        if (plan.Fingerprint != anchor.Offboarding.PlanFingerprint)
            throw new JsonException();
        var request = new FactoryOffboardingRequest(1, FactoryOffboarding.Kind, anchor.Offboarding.OperationId,
            identity.FactoryId, plan.Fingerprint, plan.AuthorityRevision, identity.AzureActorId, anchor.Offboarding.RequestedAt);
        return Task.FromResult<ExistingOffboarding?>(new(request, plan));
    }

    private static FactoryOffboardingPlan BuildPlan(
        FactoryReservationIdentity identity, Guid operation, string authorityRevision,
        FactoryReservationResult admission, IReadOnlyDictionary<string, ConfigurationSetting> authority)
    {
        var preserved = new List<string>
        {
            "application resources, identities, configuration and diagnostics",
            "repository documents, settings, labels, proposals and charter work",
            "shared owner App key, GitHub App installation and installation-wide repository access",
            "disabled factory infrastructure, protected audit, recovery records and identity",
            "owner, model, network prerequisites and third-party installations",
        };
        if (admission.Foundation?.ProductEndpoint is null)
            preserved.Add("partial onboarding recovery evidence before product manifest creation");
        var removals = new List<string>
        {
            "new live council admission",
            "GitHub-authentication workload access while retaining its disabled infrastructure",
        };
        if (admission.Plan?.OwnerAdmission is { } ownerAdmission)
            removals.AddRange(ownerAdmission.ProposedClaims.Where(claim => claim.Kind != "product")
                .OrderBy(claim => claim.Key, StringComparer.Ordinal)
                .Select(claim => $"eligible owner {claim.Kind} claim {claim.Key}"));
        foreach (var record in authority.Values.OrderBy(record => record.Key, StringComparer.Ordinal))
        {
            try
            {
                var effect = JsonSerializer.Deserialize<FoundationOperation>(record.Value, FactoryGitHubJson.Options);
                if (effect is { State: FoundationOperationState.Completed, Disposition: FoundationResourceDisposition.Created,
                        RoleDefinitionId: not null, PrincipalId: not null, RoleScope: not null })
                    removals.Add($"sole-factory grant target={effect.Target}; principal={effect.PrincipalId}; "
                        + $"role={effect.RoleDefinitionId}; scope={effect.RoleScope}; condition={effect.Condition ?? "(none)"}");
            }
            catch (JsonException) { }
        }
        if (authority.TryGetValue(ProductConfigurationKeys.FactoryGitHubAuthority, out var githubRecord))
        {
            try
            {
                var github = JsonSerializer.Deserialize<FactoryGitHubAuthority>(githubRecord.Value, FactoryGitHubJson.Options);
                if (github?.DestinationSecretUri is not null)
                    removals.Add("factory destination key version " + github.DestinationSecretUri);
            }
            catch (JsonException) { }
        }
        var blockers = new List<string>();
        if (authority.Count != 0 && !authority.ContainsKey(ProductConfigurationKeys.FactoryManifest))
            blockers.Add("product manifest missing from existing product authority");
        if (authority.Keys.Any(k => k.StartsWith("dsf/factory/", StringComparison.Ordinal)
                                    && k.Contains("unknown", StringComparison.OrdinalIgnoreCase)))
            blockers.Add("unknown factory authority records require human ownership review");
        var costs = new[]
        {
            "continuing Azure charges for retained disabled compute, storage, network, logs, vault, configuration and audit",
            "soft-deleted/recoverable vault key custody may continue until provider retention expires",
        };
        var residual = new[]
        {
            "inherited, reused, shared or human-managed grants are preserved and may retain authority",
            "other key copies and already-issued tokens are not globally invalidated",
            "shared GitHub App installation and repository selection remain unchanged",
        };
        var draft = new FactoryOffboardingPlan(1, "decide-offboarding-plan", operation, identity.FactoryId,
            identity.ProductKey, authorityRevision, preserved, removals, blockers, costs, residual, "");
        return draft with { Fingerprint = FactoryOffboarding.Fingerprint(draft) };
    }

    private async Task<(IReadOnlyList<string> Completed, IReadOnlyList<string> Blocked)> CleanupOwnedAccessAsync(
        FactoryReservationIdentity identity, IReadOnlyDictionary<string, ConfigurationSetting> authority,
        CancellationToken token)
    {
        if (!authority.TryGetValue(ProductConfigurationKeys.FactoryGitHubAuthority, out var record))
            return ([], []);
        try
        {
            var values = authority.ToDictionary(pair => pair.Key, pair => pair.Value.Value, StringComparer.Ordinal);
            var github = JsonSerializer.Deserialize<FactoryGitHubAuthority>(record.Value, FactoryGitHubJson.Options)
                ?? throw new JsonException();
            var githubEffects = FactoryGitHubAuthorityValidation.ReadCompletedEffects(github, values);
            var effects = new List<FoundationOperation>(githubEffects);
            var manifest = FactorySourceValidation.ReadRecord<FactoryManifest>(values, ProductConfigurationKeys.FactoryManifest);
            if (authority.ContainsKey(ProductConfigurationKeys.FactoryTelemetry))
            {
                var telemetry = FactoryTelemetryValidation.ReadPreparation(manifest, values);
                effects.AddRange(FactoryTelemetryValidation.ReadCompletedEffects(telemetry, values));
            }
            if (authority.ContainsKey(ProductConfigurationKeys.FactoryModels))
                effects.AddRange(FactoryModelsValidation.ReadPreparation(manifest, values).Effects ?? []);
            if (authority.ContainsKey(ProductConfigurationKeys.FactorySource))
                effects.AddRange(FactorySourceValidation.Read(manifest, github, values).Effects);
            if (authority.ContainsKey(ProductConfigurationKeys.FactorySupplemental))
            {
                var supplemental = FactorySourceValidation.ReadRecord<FactorySupplemental>(
                    values, ProductConfigurationKeys.FactorySupplemental);
                effects.AddRange(FactoryPreviewSupplementalValidation.ReadEffects(supplemental, values));
            }
            var completed = new List<string>();
            var blocked = new List<string>();
            var arm = new FactoryPreparationArm(azure, http, identity);
            foreach (var grant in effects.Where(effect => effect.RoleDefinitionId is not null)
                         .DistinctBy(effect => effect.Target, StringComparer.OrdinalIgnoreCase))
            {
                var result = await RemoveGrantAsync(arm, grant, token);
                (result.Completed ? completed : blocked).Add(result.Detail);
            }
            var key = await RemoveKeyVersionAsync(identity, githubEffects[0], token);
            (key.Completed ? completed : blocked).Add(key.Detail);
            return (completed, blocked);
        }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException
            or ArgumentException or FormatException)
        {
            return ([], ["Factory access cleanup ownership is invalid or changed; preserve grants and key custody for administrator review."]);
        }
    }

    private static async Task<(bool Completed, string Detail)> RemoveGrantAsync(
        FactoryPreparationArm arm, FoundationOperation grant, CancellationToken token)
    {
        var label = grant.LogicalIdentity == "factory:github-auth"
            ? "GitHub authentication" : "factory workload";
        try
        {
            using var current = await arm.SendAsync(HttpMethod.Get, grant.Target, "2022-04-01", token);
            if (current.StatusCode == HttpStatusCode.NotFound)
                return (true, $"{label} role assignment already absent at exact proven scope.");
            if (current.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                return (false, $"{label} role assignment removal denied; exact sole-factory grant remains.");
            if (current.StatusCode != HttpStatusCode.OK)
                return (false, $"{label} role assignment read outcome uncertain; no deletion dispatched.");
            using var document = JsonDocument.Parse(await current.Content.ReadAsStreamAsync(token),
                new JsonDocumentOptions { AllowDuplicateProperties = false });
            var root = document.RootElement;
            var properties = root.GetProperty("properties");
            var created = properties.GetProperty("createdOn").GetDateTimeOffset();
            if (!Same(root.GetProperty("id").GetString(), grant.Target)
                || properties.GetProperty("principalId").GetString() != grant.PrincipalId
                || !Same(properties.GetProperty("roleDefinitionId").GetString(), grant.RoleDefinitionId)
                || !Same(properties.GetProperty("scope").GetString(), grant.RoleScope)
                || Optional(properties, "condition") != grant.Condition
                || Optional(properties, "conditionVersion") != grant.ConditionVersion
                || !DateTimeOffset.TryParse(grant.Incarnation, out var expectedCreated)
                || created != expectedCreated)
                return (false, $"{label} role assignment changed or recreated; preserved without deletion.");

            using var deleted = await arm.SendAsync(HttpMethod.Delete, grant.Target, "2022-04-01", token);
            if (deleted.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                return (false, $"{label} role assignment removal denied; exact sole-factory grant remains.");
            if (deleted.StatusCode is not (HttpStatusCode.OK or HttpStatusCode.NoContent))
                return (false, $"{label} role assignment deletion outcome uncertain; inspect exact provider resource.");
            using var confirmed = await arm.SendAsync(HttpMethod.Get, grant.Target, "2022-04-01", token);
            return confirmed.StatusCode == HttpStatusCode.NotFound
                ? (true, $"Removed exact proven sole-factory {label} role assignment.")
                : (false, $"{label} role assignment deletion was not confirmed absent.");
        }
        catch (Exception e) when (e is HttpRequestException or IOException or JsonException
            or FormatException or InvalidOperationException or KeyNotFoundException or OperationCanceledException)
        {
            if (e is OperationCanceledException && token.IsCancellationRequested) throw;
            return (false, $"{label} role assignment cleanup response uncertain; inspect exact provider resource.");
        }
    }

    private async Task<(bool Completed, string Detail)> RemoveKeyVersionAsync(
        FactoryReservationIdentity identity, FoundationOperation key, CancellationToken token)
    {
        if (key.Endpoint is null || key.Incarnation is null
            || !key.Incarnation.StartsWith("key-version:" + key.Endpoint + ";created:", StringComparison.Ordinal))
            return (false, "Factory destination key-copy provenance is invalid; preserve key custody.");
        var createdText = key.Incarnation[("key-version:" + key.Endpoint + ";created:").Length..];
        if (!long.TryParse(createdText, System.Globalization.CultureInfo.InvariantCulture, out var createdSeconds))
            return (false, "Factory destination key-copy incarnation is invalid; preserve key custody.");
        var expectedCreated = DateTimeOffset.FromUnixTimeSeconds(createdSeconds);
        try
        {
            var credential = new OwnerReservationStore.OperatorCredential(azure, identity.TenantId,
                identity.SubscriptionId, identity.AzureActorId, "https://vault.azure.net");
            var access = await credential.GetTokenAsync(
                new TokenRequestContext(["https://vault.azure.net/.default"]), token);
            async Task<HttpResponseMessage> SendAsync(HttpMethod method)
            {
                using var request = new HttpRequestMessage(method, key.Endpoint + "?api-version=7.5");
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", access.Token);
                return await (http ?? ProviderHttp).SendAsync(request, token);
            }
            using var current = await SendAsync(HttpMethod.Get);
            if (current.StatusCode == HttpStatusCode.NotFound)
                return (true, "Factory destination key version already absent; recoverable custody remains provider-defined.");
            if (current.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                return (false, "Factory destination key-version removal denied; shared owner key remains untouched.");
            if (current.StatusCode != HttpStatusCode.OK)
                return (false, "Factory destination key-version read outcome uncertain; no deletion dispatched.");
            using var document = JsonDocument.Parse(await current.Content.ReadAsStreamAsync(token),
                new JsonDocumentOptions { AllowDuplicateProperties = false });
            var root = document.RootElement;
            var created = DateTimeOffset.FromUnixTimeSeconds(root.GetProperty("attributes").GetProperty("created").GetInt64());
            if (root.GetProperty("id").GetString() != key.Endpoint || created != expectedCreated)
                return (false, "Factory destination key version changed or recreated; preserved without deletion.");

            using var deleted = await SendAsync(HttpMethod.Delete);
            if (deleted.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                return (false, "Factory destination key-version removal denied; shared owner key remains untouched.");
            if (deleted.StatusCode != HttpStatusCode.OK)
                return (false, "Factory destination key-version deletion outcome uncertain; inspect vault recovery state.");
            using var confirmed = await SendAsync(HttpMethod.Get);
            return confirmed.StatusCode == HttpStatusCode.NotFound
                ? (true, "Removed exact proven factory destination key version; soft-deleted/recoverable custody may remain.")
                : (false, "Factory destination key-version deletion was not confirmed absent.");
        }
        catch (Exception e) when (e is HttpRequestException or IOException or JsonException
            or FormatException or InvalidOperationException or KeyNotFoundException or OperationCanceledException)
        {
            if (e is OperationCanceledException && token.IsCancellationRequested) throw;
            return (false, "Factory destination key-version cleanup response uncertain; inspect vault recovery state.");
        }
    }

    private static bool Same(string? left, string? right) =>
        string.Equals(left?.TrimEnd('/'), right?.TrimEnd('/'), StringComparison.OrdinalIgnoreCase);

    private static string? Optional(JsonElement properties, string name) =>
        !properties.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null
            ? null : value.GetString();

    private static FactoryOffboardingResult Result(
        FactoryOffboardingState state, string detail, FactoryOffboardingPlan plan,
        FactoryOffboardingRequest? request, bool admissionClosed, bool registrationRetained) =>
        new(state, detail)
        {
            Plan = plan, Request = request, AdmissionClosed = admissionClosed,
            RegistrationRetained = registrationRetained,
        };
}
