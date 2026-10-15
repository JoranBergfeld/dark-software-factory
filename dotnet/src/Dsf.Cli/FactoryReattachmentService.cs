using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Azure.Core;
using Azure.Data.AppConfiguration;
using Dsf.Core.Onboarding;
using Dsf.Core.Runtime;

namespace Dsf.Cli;

internal sealed class FactoryReattachmentService(
    RepositoryAttachmentReviewer? reviewer, IAzureCliRunner azure, HttpClient? http, TimeProvider clock)
{
    private static readonly HttpClient ProviderHttp = new(new HttpClientHandler { AllowAutoRedirect = false });

    internal async Task<FactoryReattachmentResult> ExecuteAsync(
        FactoryReservationResult admission, Guid operation, string? approval, CancellationToken token)
    {
        if (admission.Identity is not { } identity || admission.Plan is null || admission.Revision is null)
            return Result(FactoryReattachmentState.Blocked,
                "Trusted retained provisioned factory identity and current remote plan required; partial onboarding is not promoted by reattachment.");

        var owner = new OwnerReservationStore(identity.OwnerEndpoint, identity.TenantId, identity.SubscriptionId,
            azure, http, identity.AzureActorId);
        if (await owner.ReadAsync(FactoryPurge.RetirementKey(identity.FactoryId), token) is not null
            || await owner.ReadAsync(FactoryReattachment.RetirementKey(identity.FactoryId), token) is not null)
            return Result(FactoryReattachmentState.Blocked,
                "Factory identity is retired; retained names or infrastructure cannot resurrect it.");
        var ownerRoot = await owner.ReadAsync(ProductConfigurationKeys.OwnerProductClaim(identity.ProductKey), token);
        if (ownerRoot is null || ownerRoot.ETag.ToString() != admission.Revision)
            return Result(FactoryReattachmentState.Blocked,
                "Owner registration changed during reattachment review; no claim or access write.");
        var anchor = FactoryReservationRecords.Parse<FactoryReservationAnchor>(ownerRoot.Value);
        if (anchor.Identity != identity) throw new JsonException();
        if (anchor.Purge is not null)
            return Result(FactoryReattachmentState.Blocked,
                "Factory identity is in durable purge recovery; retained infrastructure cannot reattach.");
        var endpoint = admission.Foundation?.ProductEndpoint ?? anchor.Foundation?.ProductEndpoint;
        if (anchor.Foundation is not { State: FactoryFoundationState.Inactive } || endpoint is null)
            return Result(FactoryReattachmentState.Blocked,
                "Trusted retained provisioned factory identity required; partial onboarding is not promoted by reattachment.");

        var product = new OwnerReservationStore(endpoint, identity.TenantId, identity.SubscriptionId,
            azure, http, identity.AzureActorId);
        var authority = await ReadAuthorityAsync(product, token);
        if (!authority.TryGetValue(ProductConfigurationKeys.FactoryManifest, out var manifestRecord)
            || FactoryReservationRecords.Parse<FactoryManifest>(manifestRecord.Value).Identity != identity)
            return Result(FactoryReattachmentState.Blocked,
                "Retained product manifest is missing or identifies another factory; no adoption.");

        FactoryReattachmentPlan plan;
        FactoryReattachmentRequest request;
        var resuming = false;
        if (authority.TryGetValue(FactoryReattachment.Key, out var retainedRecord))
        {
            var retained = FactoryReattachment.Read(retainedRecord.Value, identity);
            if (retained.OperationId != operation)
                return Result(FactoryReattachmentState.Blocked,
                    "Another protected reattachment operation already owns recovery; no takeover.")
                with { Request = retained, AdmissionOpen = anchor.Reattachment is not null };
            var retainedPlanRecord = await product.ReadAsync(FactoryReattachment.PlanKey(operation), token);
            plan = retainedPlanRecord is null
                ? throw new JsonException()
                : JsonSerializer.Deserialize<FactoryReattachmentPlan>(
                    retainedPlanRecord.Value, FactoryGitHubJson.Options) ?? throw new JsonException();
            if (plan.Fingerprint != retained.PlanFingerprint
                || FactoryReattachment.Fingerprint(plan) != retained.PlanFingerprint)
                throw new JsonException();
            if (anchor.Reattachment is not null)
                return Result(FactoryReattachmentState.AwaitingReadiness,
                    "Same identity is reattached; historical readiness and approval remain invalid.")
                with { Request = retained, Plan = plan, AdmissionOpen = true };
            if (string.IsNullOrWhiteSpace(approval))
                return Result(FactoryReattachmentState.ReviewRequired,
                    "Partial reattachment recovery requires the original exact reviewed fingerprint.")
                with { Request = retained, Plan = plan, AdmissionOpen = false };
            if (approval != retained.PlanFingerprint)
                return Result(FactoryReattachmentState.Blocked,
                    "Approval differs from the retained reattachment plan; no recovery replay.")
                with { Request = retained, Plan = plan, AdmissionOpen = false };
            request = retained;
            resuming = true;
        }
        else
        {
            if (anchor.Offboarding is not { State: FactoryOffboardingState.Detached })
                return Result(FactoryReattachmentState.Blocked,
                    "Only a fully detached, non-purged factory may reattach; no automatic resurrection.");
            plan = await BuildPlanAsync(admission, owner, ownerRoot, product, authority, operation, token);
            if (plan.DriftFindings.Count != 0)
                return Result(FactoryReattachmentState.Blocked,
                    "Current drift, ownership or access review found blockers; no claim takeover or identity retargeting.")
                with { Plan = plan, BlockedActions = plan.DriftFindings };
            if (string.IsNullOrWhiteSpace(approval))
                return Result(FactoryReattachmentState.ReviewRequired,
                    "Current same-identity reattachment review complete; no claims, access or admission changed.")
                with { Plan = plan };
            if (approval != plan.Fingerprint)
                return Result(FactoryReattachmentState.Blocked,
                    "Approval differs from the exact current reattachment plan; no effects dispatched.")
                with { Plan = plan };
            request = new FactoryReattachmentRequest(1, FactoryReattachment.Kind, operation,
                identity.FactoryId, plan.Fingerprint, plan.AuthorityRevision, identity.AzureActorId, clock.GetUtcNow());
        }
        authority.TryGetValue(FactoryOffboarding.Key, out var offboardingRoot);
        var offboardingValue = offboardingRoot?.Value
            ?? await ReadRetiredValueAsync(product, operation, FactoryOffboarding.Key, token);
        if (offboardingValue is null)
            return Result(FactoryReattachmentState.Unknown,
                "Detached owner registration lacks exact product offboarding authority; absence cannot authorize recovery.")
            with { Plan = plan, Request = request };
        var offboarding = FactoryOffboarding.Read(offboardingValue, identity);
        if (anchor.Offboarding is null || offboarding.OperationId != anchor.Offboarding.OperationId
            || offboarding.PlanFingerprint != anchor.Offboarding.PlanFingerprint)
            throw new JsonException();
        var value = FactoryReservationRecords.Serialize(request);
        _ = FactoryReattachment.Read(value, identity);
        var completed = new List<string>();
        try
        {
            await FactoryLifecycleCoordinator.AcquireAsync(product, identity,
                FactoryLifecycleCoordinator.Reattachment, operation, clock, token);
            if (!resuming)
            {
                await product.WriteAsync(FactoryReattachment.PlanKey(operation),
                    FactoryReservationRecords.Serialize(plan), null, token);
                await product.WriteAsync(FactoryReattachment.Key, value, null, token);
                await product.WriteAsync(FactoryReattachment.HistoryKey(operation), value, null, token);
            }

            var planBytes = Encoding.UTF8.GetBytes(BoundaryPlanFingerprint.Canonical(
                JsonSerializer.SerializeToElement(admission.Plan, FactoryReservationRecords.Json)));
            foreach (var step in FactoryReservationRecords.Steps(anchor, planBytes, admission.Plan)
                         .Skip(anchor.PlanChunks))
            {
                var existing = await owner.ReadAsync(step.Key, token);
                if (existing is null)
                    await owner.WriteAsync(step.Key, step.Value, null, token);
                else if (existing.Value != step.Value)
                    throw new ReservationStoreException(FactoryReservationState.Blocked,
                        "Competing owner claim appeared after review; no overwrite or release.");
                completed.Add("Acquired exact eligible owner registry claim " + step.Key + ".");
            }

            var access = await RestoreOwnedAccessAsync(product, identity, authority, operation, token);
            completed.AddRange(access.Completed);
            var blockedAccess = access.Blocked.ToList();
            var key = await RestoreDestinationKeyAsync(identity, authority, token);
            (key.Completed ? completed : blockedAccess).Add(key.Detail);
            if (blockedAccess.Count != 0)
                return Result(FactoryReattachmentState.Partial,
                    "Eligible claims were reacquired, but exact factory access recovery is incomplete; admission remains closed.")
                with
                {
                    Plan = plan, Request = request, CompletedActions = completed,
                    BlockedActions = blockedAccess, AdmissionOpen = false,
                };

            await RetireStaleGenerationAsync(product, request, authority, token);
            var currentManifestRecord = await product.ReadAsync(ProductConfigurationKeys.FactoryManifest, token)
                ?? throw new JsonException();
            var currentManifest = FactoryReservationRecords.Parse<FactoryManifest>(currentManifestRecord.Value);
            if (currentManifest.Identity != identity) throw new JsonException();
            if (currentManifest.PreviewOperationId is not null || currentManifest.PreviewApprovalFingerprint is not null)
                await product.WriteAsync(currentManifestRecord.Key, FactoryReservationRecords.Serialize(currentManifest with
                {
                    PreviewOperationId = null,
                    PreviewApprovalFingerprint = null,
                }), currentManifestRecord.ETag, token);
            await InvalidateReadinessAsync(product, identity, token);
            completed.Add("Retired stale preview and activation authority while preserving audit hashes.");

            var currentRoot = await owner.ReadAsync(ownerRoot.Key, token)
                ?? throw new ReservationStoreException(FactoryReservationState.Unknown,
                    "Owner registration disappeared during reattachment.");
            if (currentRoot.ETag != ownerRoot.ETag || currentRoot.Value != ownerRoot.Value)
                throw new ReservationStoreException(FactoryReservationState.Blocked,
                    "Owner registration changed during reattachment; no overwrite.");
            var updated = anchor with
            {
                Offboarding = null,
                Reattachment = new(operation, FactoryReattachmentState.AwaitingReadiness,
                    plan.Fingerprint, plan.AuthorityRevision, request.RequestedAt),
            };
            await owner.WriteAsync(ownerRoot.Key, FactoryReservationRecords.Serialize(updated), ownerRoot.ETag, token);
            await FactoryLifecycleCoordinator.CompleteAsync(product, identity,
                FactoryLifecycleCoordinator.Reattachment, operation,
                FactoryLifecycleCoordinator.Completed, clock, token);
            completed.Add("Reopened preparation admission without activation.");
            return Result(FactoryReattachmentState.AwaitingReadiness,
                "Same immutable factory/repository/application identity reattached. Old readiness, preview and activation remain history.")
            with { Plan = plan, Request = request, AdmissionOpen = true, CompletedActions = completed };
        }
        catch (ReservationStoreException e)
        {
            return Result(e.State switch
            {
                FactoryReservationState.Blocked => FactoryReattachmentState.Blocked,
                FactoryReservationState.Failed => FactoryReattachmentState.Failed,
                _ => FactoryReattachmentState.Unknown,
            }, e.Message + " Reattachment may be partial; inspect the original operation.")
            with { Plan = plan, Request = request, CompletedActions = completed };
        }
    }

    private async Task<FactoryReattachmentPlan> BuildPlanAsync(
        FactoryReservationResult admission, OwnerReservationStore owner, ConfigurationSetting ownerRoot,
        OwnerReservationStore product, IReadOnlyDictionary<string, ConfigurationSetting> authority,
        Guid operation, CancellationToken token)
    {
        var identity = admission.Identity!;
        var anchor = FactoryReservationRecords.Parse<FactoryReservationAnchor>(ownerRoot.Value);
        var planBytes = Encoding.UTF8.GetBytes(BoundaryPlanFingerprint.Canonical(
            JsonSerializer.SerializeToElement(admission.Plan!, FactoryReservationRecords.Json)));
        if (FactoryReservationRecords.Hash(planBytes) != anchor.PlanSha256) throw new JsonException();
        var proof = new ReservationAdmissionProof();
        proof.Add(ownerRoot);
        for (var i = 0; i < anchor.PlanChunks; i++)
        {
            var chunk = await owner.ReadAsync(ProductConfigurationKeys.OwnerReservationPlanChunk(identity.FactoryId, i), token)
                ?? throw new JsonException();
            proof.Add(chunk);
        }
        var fresh = await new ApplicationBoundaryPlanner(reviewer, new AzureBoundaryReader(azure))
            .PlanAsync(admission.Plan!.Selection, token, reservationProof: proof);
        var findings = new List<string>();
        if (fresh.State != BoundaryPlanState.Reviewed)
            findings.AddRange(fresh.Findings.Select(finding => finding.Check + ": " + finding.Detail));
        if (fresh.RepositoryReview.Repository?.Id != identity.RepositoryId
            || fresh.Selection.SubscriptionId != identity.SubscriptionId
            || fresh.Selection.ApplicationEnvironment != identity.ApplicationEnvironment)
            findings.Add("NewOnboardingRequired: repository, subscription or application environment identity differs.");

        var claims = new List<string>();
        foreach (var step in FactoryReservationRecords.Steps(anchor, planBytes, admission.Plan)
                     .Skip(anchor.PlanChunks))
        {
            var existing = await owner.ReadAsync(step.Key, token);
            if (existing is not null && existing.Value != step.Value)
                findings.Add("Competing owner claim: " + step.Key + ".");
            claims.Add(step.Key);
        }
        var probes = await ProbeCurrentAuthorityAsync(admission, authority, findings, token);
        var access = authority.Values.Select(record =>
        {
            try { return JsonSerializer.Deserialize<FoundationOperation>(record.Value, FactoryGitHubJson.Options); }
            catch (JsonException) { return null; }
        }).Where(effect => effect is
        {
            State: FoundationOperationState.Completed,
            Disposition: FoundationResourceDisposition.Created,
            RoleDefinitionId: not null,
        }).Select(effect => effect!.Target).Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase).Concat(probes).ToArray();
        var revision = FactoryModelsValidation.Hash(new
        {
            Owner = ownerRoot.ETag.ToString(),
            Product = authority.OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .Select(pair => new { pair.Key, ETag = pair.Value.ETag.ToString(), Hash = FactoryReservationRecords.Hash(pair.Value.Value) }),
            Fresh = fresh.Fingerprint,
        });
        var plan = new FactoryReattachmentPlan(1, FactoryReattachment.Kind, operation, identity.FactoryId,
            identity.RepositoryId, identity.ApplicationEnvironment, revision, claims, access, findings,
            [
                "application resources, configuration and delivery ownership",
                "repository identity, documents, settings, issues and human outcomes",
                "factory identity, retained infrastructure, audit and publication provenance",
                "shared owner assets and GitHub App installation",
            ], "");
        return plan with { Fingerprint = FactoryReattachment.Fingerprint(plan) };
    }

    private async Task<IReadOnlyList<string>> ProbeCurrentAuthorityAsync(
        FactoryReservationResult admission, IReadOnlyDictionary<string, ConfigurationSetting> authority,
        ICollection<string> findings, CancellationToken token)
    {
        var observations = new List<string>();
        var identity = admission.Identity!;
        if (admission.Foundation is not { State: FactoryFoundationState.Inactive } foundation)
            findings.Add("RetainedInfrastructure: provisioned inactive foundation is not current.");
        else
        foreach (var operation in foundation.Operations.Where(operation =>
                     operation.Disposition == FoundationResourceDisposition.Created))
            if (operation.State != FoundationOperationState.Completed)
                findings.Add("RetainedInfrastructure: current incarnation unconfirmed for " + operation.Target + ".");

        if (!authority.TryGetValue(ProductConfigurationKeys.FactoryGitHubAuthority, out var record))
        {
            findings.Add("AppSourceAccess: retained GitHub App authority is missing.");
            return observations;
        }
        try
        {
            var github = JsonSerializer.Deserialize<FactoryGitHubAuthority>(
                record.Value, FactoryGitHubJson.Options) ?? throw new JsonException();
            var effects = FactoryGitHubAuthorityValidation.ReadCompletedEffects(github,
                authority.ToDictionary(pair => pair.Key, pair => pair.Value.Value, StringComparer.Ordinal));
            var arm = new FactoryPreparationArm(azure, http, identity);
            foreach (var grant in effects.Where(effect => effect.RoleDefinitionId is not null))
            {
                using var response = await arm.SendAsync(HttpMethod.Get, grant.Target, grant.ApiVersion, token);
                if (response.StatusCode == HttpStatusCode.NotFound)
                    observations.Add("grant:" + grant.Target + ":missing-recoverable");
                else if (response.StatusCode != HttpStatusCode.OK)
                    findings.Add("GrantCustody: exact retained assignment is unreadable: " + grant.Target + ".");
                else if (!(await ReadGrantAsync(response, grant, token)).Matches)
                    findings.Add("GrantCustody: exact retained assignment conflicts: " + grant.Target + ".");
                else observations.Add("grant:" + grant.Target + ":exact");
            }
            if (github.DestinationSecretUri is { } key)
            {
                var credential = new OwnerReservationStore.OperatorCredential(
                    azure, identity.TenantId, identity.SubscriptionId, identity.AzureActorId,
                    "https://vault.azure.net");
                var access = await credential.GetTokenAsync(
                    new TokenRequestContext(["https://vault.azure.net/.default"]), token);
                using var request = new HttpRequestMessage(HttpMethod.Get, key + "?api-version=7.5");
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", access.Token);
                using var response = await (http ?? ProviderHttp).SendAsync(request, token);
                if (response.StatusCode == HttpStatusCode.OK)
                {
                    using var document = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(token));
                    var root = document.RootElement;
                    if (root.GetProperty("id").GetString() != key
                        || !root.GetProperty("attributes").GetProperty("enabled").GetBoolean())
                        findings.Add("KeyCustody: retained destination key version conflicts with exact custody.");
                    else observations.Add("key:" + key + ":exact");
                }
                else if (response.StatusCode == HttpStatusCode.NotFound) observations.Add("key:" + key + ":missing-recoverable");
                else findings.Add("KeyCustody: exact retained destination key version is unreadable.");
            }
            else findings.Add("KeyCustody: retained destination key version identity is missing.");
            var hostEffect = effects.SingleOrDefault(effect =>
                string.Equals(effect.ResourceId, github.HostResourceId, StringComparison.OrdinalIgnoreCase));
            if (hostEffect is null)
                findings.Add("RetainedInfrastructure: GitHub App host creation receipt is missing.");
            else
            {
                using var response = await arm.SendAsync(HttpMethod.Get, hostEffect.ResourceId!, hostEffect.ApiVersion, token);
                if (response.StatusCode != HttpStatusCode.OK)
                    findings.Add("RetainedInfrastructure: GitHub App host is missing or unreadable.");
                else
                {
                    using var document = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(token));
                    var created = document.RootElement.GetProperty("systemData").GetProperty("createdAt")
                        .GetDateTimeOffset().ToString("O");
                    var endpoint = "https://" + document.RootElement.GetProperty("properties")
                        .GetProperty("configuration").GetProperty("ingress").GetProperty("fqdn").GetString();
                    if (created != hostEffect.Incarnation || endpoint != github.HostEndpoint)
                        findings.Add("RetainedInfrastructure: GitHub App host incarnation changed.");
                    else observations.Add("host:" + hostEffect.ResourceId + ":" + created);
                }
            }

            if (github.HostEndpoint is null || string.IsNullOrWhiteSpace(github.AudienceClientId))
                findings.Add("AppSourceAccess: retained GitHub App verification endpoint is incomplete.");
            else
            {
                var credential = new OwnerReservationStore.OperatorCredential(
                    azure, identity.TenantId, identity.SubscriptionId, identity.AzureActorId,
                    "api://" + github.AudienceClientId);
                var access = await credential.GetTokenAsync(
                    new TokenRequestContext(["api://" + github.AudienceClientId + "/.default"]), token);
                using var request = new HttpRequestMessage(HttpMethod.Post, github.HostEndpoint + "/verify");
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", access.Token);
                using var response = await (http ?? ProviderHttp).SendAsync(request, token);
                if (response.StatusCode != HttpStatusCode.OK)
                    findings.Add("AppSourceAccess: GitHub App installation, permissions, repository source access or network path is revoked.");
                else
                {
                    var proof = JsonSerializer.Deserialize<GitHubAuthorityProof>(
                        await response.Content.ReadAsStringAsync(token), FactoryGitHubJson.Options);
                    if (proof is null || !FactoryGitHubAuthorityValidation.HasVerifiedScope(github, proof))
                        findings.Add("AppSourceAccess: GitHub App installation, permissions or repository source scope changed.");
                    else observations.Add("app-source:" + FactoryModelsValidation.Hash(proof));
                }
            }
        }
        catch (Exception e) when (e is JsonException or HttpRequestException or IOException
            or InvalidOperationException or KeyNotFoundException
            or OwnerReservationStore.AuthenticationFailedException)
        {
            findings.Add("AppSourceAccess: current GitHub App/source/network proof is unavailable.");
        }
        return observations.Order(StringComparer.Ordinal).ToArray();
    }

    private static async Task<IReadOnlyDictionary<string, ConfigurationSetting>> ReadAuthorityAsync(
        OwnerReservationStore product, CancellationToken token)
    {
        var records = new Dictionary<string, ConfigurationSetting>(
            await product.ReadFactoryAuthorityAsync(token), StringComparer.Ordinal);
        foreach (var prefix in new[]
                 {
                     FactoryActivation.Prefix, FactoryLive.Prefix, FactoryPublisher.Prefix,
                     FactoryPromotion.Prefix, FactoryOffboarding.HistoryPrefix,
                 })
        foreach (var pair in await product.ReadNamespaceAsync(
                     prefix, token, maximumRecords: FactoryAudit.GovernanceHistoryRecords))
            records[pair.Key] = pair.Value;
        return records;
    }

    private static async Task RetireStaleGenerationAsync(
        OwnerReservationStore product, FactoryReattachmentRequest request,
        IReadOnlyDictionary<string, ConfigurationSetting> authority, CancellationToken token)
    {
        const int chunkBytes = FactoryReconfigurationOperationRecords.ChunkBytes;
        var stale = authority.Values.Where(IsStaleGeneration)
            .OrderBy(record => record.Key, StringComparer.Ordinal).ToArray();
        var progressKey = FactoryReattachment.RetirementProgressKey(request.OperationId);
        var progressRecord = await product.ReadAsync(progressKey, token);
        FactoryReattachmentRetirementProgress progress;
        if (progressRecord is null)
        {
            for (var index = 0; index < stale.Length; index++)
                await ArchiveAsync(stale[index], index);
            progress = new(1, request.OperationId, request.PlanFingerprint, stale.Length, 0);
            progressRecord = await product.WriteAsync(progressKey,
                FactoryReservationRecords.Serialize(progress), null, token);
        }
        else
        {
            progress = FactoryReservationRecords.Parse<FactoryReattachmentRetirementProgress>(progressRecord.Value);
            if (progress.Protocol != 1 || progress.OperationId != request.OperationId
                || progress.PlanFingerprint != request.PlanFingerprint || progress.Records < 0
                || progress.Applied < 0 || progress.Applied > progress.Records)
                throw new JsonException();
        }

        for (var index = progress.Applied; index < progress.Records; index++)
        {
            var retired = await ReadArchivedAsync(index);
            var current = await product.ReadAsync(retired.Record.Key, token);
            if (current is not null)
            {
                if (current.ETag.ToString() != retired.Record.ETag
                    || FactoryReservationRecords.Hash(current.Value) != retired.Record.Sha256
                    || current.Value != retired.Value)
                    throw new ReservationStoreException(FactoryReservationState.Blocked,
                        "ConcurrentGenerationChange: stale authority changed; no deletion.");
                await product.DeleteReattachmentGenerationAsync(current, token);
            }
            progress = progress with { Applied = index + 1 };
            progressRecord = await product.WriteAsync(progressKey,
                FactoryReservationRecords.Serialize(progress), progressRecord.ETag, token);
        }
        return;

        async Task ArchiveAsync(ConfigurationSetting record, int index)
        {
            var bytes = Encoding.UTF8.GetBytes(record.Value);
            var descriptor = new FactoryReattachmentRetiredRecord(
                1, request.OperationId, index, record.Key, record.ETag.ToString(),
                FactoryReservationRecords.Hash(record.Value), bytes.Length,
                (bytes.Length + chunkBytes - 1) / chunkBytes);
            await EnsureAsync(FactoryReattachment.RetiredKey(request.OperationId, index),
                FactoryReservationRecords.Serialize(descriptor));
            for (var chunk = 0; chunk < descriptor.Chunks; chunk++)
            {
                var value = new FactoryReattachmentRetiredChunk(
                    1, request.OperationId, index, chunk,
                    Convert.ToBase64String(bytes.AsSpan(chunk * chunkBytes,
                        Math.Min(chunkBytes, bytes.Length - chunk * chunkBytes))));
                await EnsureAsync(FactoryReattachment.RetiredChunkKey(request.OperationId, index, chunk),
                    FactoryReservationRecords.Serialize(value));
            }
        }

        async Task EnsureAsync(string key, string value)
        {
            var existing = await product.ReadAsync(key, token);
            if (existing is null) await product.WriteAsync(key, value, null, token);
            else if (existing.Value != value) throw new JsonException();
        }

        async Task<(FactoryReattachmentRetiredRecord Record, string Value)> ReadArchivedAsync(int index)
        {
            var descriptor = FactoryReservationRecords.Parse<FactoryReattachmentRetiredRecord>(
                (await product.ReadAsync(FactoryReattachment.RetiredKey(request.OperationId, index), token))?.Value
                ?? throw new JsonException());
            if (descriptor.Protocol != 1 || descriptor.OperationId != request.OperationId
                || descriptor.Index != index || descriptor.Bytes < 0
                || descriptor.Chunks != (descriptor.Bytes + chunkBytes - 1) / chunkBytes)
                throw new JsonException();
            using var content = new MemoryStream();
            for (var chunk = 0; chunk < descriptor.Chunks; chunk++)
            {
                var archived = FactoryReservationRecords.Parse<FactoryReattachmentRetiredChunk>(
                    (await product.ReadAsync(
                        FactoryReattachment.RetiredChunkKey(request.OperationId, index, chunk), token))?.Value
                    ?? throw new JsonException());
                var bytes = Convert.FromBase64String(archived.Data);
                if (archived.Protocol != 1 || archived.OperationId != request.OperationId
                    || archived.Record != index || archived.Index != chunk
                    || bytes.Length != Math.Min(chunkBytes, descriptor.Bytes - chunk * chunkBytes))
                    throw new JsonException();
                content.Write(bytes);
            }
            var value = Encoding.UTF8.GetString(content.ToArray());
            if (content.Length != descriptor.Bytes
                || FactoryReservationRecords.Hash(value) != descriptor.Sha256)
                throw new JsonException();
            return (descriptor, value);
        }

        static bool IsStaleGeneration(ConfigurationSetting record) =>
            record.Key.StartsWith(ProductConfigurationKeys.FactoryPreview, StringComparison.Ordinal)
            || record.Key.StartsWith(FactoryPreviewPreparation.Namespace, StringComparison.Ordinal)
            || record.Key.StartsWith(FactoryPreviewPublication.Namespace, StringComparison.Ordinal)
            || record.Key.StartsWith(FactoryActivation.Prefix, StringComparison.Ordinal)
            || record.Key.StartsWith(FactoryLive.Prefix, StringComparison.Ordinal)
            || record.Key.StartsWith(FactoryPublisher.Prefix, StringComparison.Ordinal)
            || record.Key.StartsWith(FactoryPromotion.Prefix, StringComparison.Ordinal)
            || record.Key == FactoryOffboarding.Key
            || record.Key.StartsWith(FactoryOffboarding.HistoryPrefix, StringComparison.Ordinal)
            || record.Key == ProductConfigurationKeys.CharterAdopted
            || record.Key == ProductConfigurationKeys.CharterAdoptionOperation;
    }

    private static async Task<string?> ReadRetiredValueAsync(
        OwnerReservationStore product, Guid operation, string expectedKey, CancellationToken token)
    {
        const int chunkBytes = FactoryReconfigurationOperationRecords.ChunkBytes;
        var progressRecord = await product.ReadAsync(
            FactoryReattachment.RetirementProgressKey(operation), token);
        if (progressRecord is null) return null;
        var progress = FactoryReservationRecords.Parse<FactoryReattachmentRetirementProgress>(progressRecord.Value);
        if (progress.Protocol != 1 || progress.OperationId != operation
            || progress.Records < 0 || progress.Applied < 0 || progress.Applied > progress.Records)
            throw new JsonException();
        for (var index = 0; index < progress.Records; index++)
        {
            var descriptor = FactoryReservationRecords.Parse<FactoryReattachmentRetiredRecord>(
                (await product.ReadAsync(FactoryReattachment.RetiredKey(operation, index), token))?.Value
                ?? throw new JsonException());
            if (descriptor.Key != expectedKey) continue;
            using var content = new MemoryStream();
            for (var chunk = 0; chunk < descriptor.Chunks; chunk++)
            {
                var archived = FactoryReservationRecords.Parse<FactoryReattachmentRetiredChunk>(
                    (await product.ReadAsync(FactoryReattachment.RetiredChunkKey(operation, index, chunk), token))?.Value
                    ?? throw new JsonException());
                var bytes = Convert.FromBase64String(archived.Data);
                if (archived.Protocol != 1 || archived.OperationId != operation
                    || archived.Record != index || archived.Index != chunk
                    || bytes.Length != Math.Min(chunkBytes, descriptor.Bytes - chunk * chunkBytes))
                    throw new JsonException();
                content.Write(bytes);
            }
            var value = Encoding.UTF8.GetString(content.ToArray());
            if (descriptor.Protocol != 1 || descriptor.OperationId != operation
                || descriptor.Index != index || descriptor.Bytes != content.Length
                || descriptor.Chunks != (descriptor.Bytes + chunkBytes - 1) / chunkBytes
                || FactoryReservationRecords.Hash(value) != descriptor.Sha256)
                throw new JsonException();
            return value;
        }
        return null;
    }

    private async Task<(IReadOnlyList<string> Completed, IReadOnlyList<string> Blocked)> RestoreOwnedAccessAsync(
        OwnerReservationStore product, FactoryReservationIdentity identity,
        IReadOnlyDictionary<string, ConfigurationSetting> authority,
        Guid operation, CancellationToken token)
    {
        var effects = authority.Values.Select(record =>
        {
            try { return JsonSerializer.Deserialize<FoundationOperation>(record.Value, FactoryGitHubJson.Options); }
            catch (JsonException) { return null; }
        }).Where(effect => effect is
        {
            State: FoundationOperationState.Completed,
            Disposition: FoundationResourceDisposition.Created,
            RoleDefinitionId: not null,
            PrincipalId: not null,
            RoleScope: not null,
        }).DistinctBy(effect => effect!.Target, StringComparer.OrdinalIgnoreCase).ToArray();
        var completed = new List<string>();
        var blocked = new List<string>();
        var arm = new FactoryPreparationArm(azure, http, identity);
        for (var index = 0; index < effects.Length; index++)
        {
            var effect = effects[index]!;
            try
            {
                using var current = await arm.SendAsync(HttpMethod.Get, effect.Target, effect.ApiVersion, token);
                if (current.StatusCode == HttpStatusCode.OK)
                {
                    if (!(await ReadGrantAsync(current, effect, token)).Matches)
                        blocked.Add("Existing role assignment conflicts with exact retained factory access: " + effect.Target + ".");
                    else
                        completed.Add("Confirmed exact retained role assignment " + effect.Target + ".");
                    continue;
                }
                if (current.StatusCode != HttpStatusCode.NotFound)
                {
                    blocked.Add("Exact role assignment absence or ownership is unconfirmed: " + effect.Target + ".");
                    continue;
                }
                var body = JsonSerializer.Serialize(new
                {
                    properties = new
                    {
                        roleDefinitionId = effect.RoleDefinitionId,
                        principalId = effect.PrincipalId,
                        principalType = "ServicePrincipal",
                        condition = effect.Condition,
                        conditionVersion = effect.ConditionVersion,
                    },
                });
                var requestId = Guid.NewGuid();
                using var created = await arm.SendAsync(HttpMethod.Put, effect.Target, effect.ApiVersion,
                    token, body, requestId);
                if (created.StatusCode is not (HttpStatusCode.OK or HttpStatusCode.Created))
                {
                    blocked.Add("Exact role assignment recreation was denied or uncertain: " + effect.Target + ".");
                    continue;
                }
                using var confirmed = await arm.SendAsync(HttpMethod.Get, effect.Target, effect.ApiVersion, token);
                var readback = confirmed.StatusCode == HttpStatusCode.OK
                    ? await ReadGrantAsync(confirmed, effect, token)
                    : default;
                if (confirmed.StatusCode != HttpStatusCode.OK || !readback.Matches)
                {
                    blocked.Add("Recreated role assignment readback differs from reviewed access: " + effect.Target + ".");
                    continue;
                }
                var recovery = effect with
                {
                    Kind = "decide-reattachment-access",
                    OperationId = operation,
                    Index = index,
                    RequestSha256 = FactoryReservationRecords.Hash(body),
                    ClientRequestId = requestId,
                    StartedAt = clock.GetUtcNow(),
                    Incarnation = readback.CreatedOn.ToString("O"),
                    ProviderRequestId = null,
                    ProviderCorrelationId = null,
                    ProviderOperationId = null,
                };
                await product.WriteAsync(FactoryReattachment.AccessKey(operation, index),
                    FactoryReservationRecords.Serialize(recovery), null, token);
                completed.Add("Reacquired exact factory role assignment " + effect.Target + ".");
            }
            catch (Exception e) when (e is HttpRequestException or IOException or JsonException
                or FormatException or InvalidOperationException or KeyNotFoundException or OperationCanceledException)
            {
                if (e is OperationCanceledException && token.IsCancellationRequested) throw;
                blocked.Add("Exact role assignment recovery outcome is unknown: " + effect.Target + ".");
            }
        }
        return (completed, blocked);
    }

    private async Task<(bool Completed, string Detail)> RestoreDestinationKeyAsync(
        FactoryReservationIdentity identity, IReadOnlyDictionary<string, ConfigurationSetting> authority,
        CancellationToken token)
    {
        try
        {
            if (!authority.TryGetValue(ProductConfigurationKeys.FactoryGitHubAuthority, out var record))
                return (false, "Retained GitHub authority missing; destination key access cannot be recovered.");
            var github = JsonSerializer.Deserialize<FactoryGitHubAuthority>(record.Value, FactoryGitHubJson.Options)
                ?? throw new JsonException();
            if (github.DestinationSecretUri is not { } expected)
                return (false, "Retained destination key-version identity missing; no name-based recovery.");
            var credential = new OwnerReservationStore.OperatorCredential(azure, identity.TenantId,
                identity.SubscriptionId, identity.AzureActorId, "https://vault.azure.net");
            var access = await credential.GetTokenAsync(
                new TokenRequestContext(["https://vault.azure.net/.default"]), token);
            async Task<HttpResponseMessage> SendAsync(HttpMethod method, string uri)
            {
                using var request = new HttpRequestMessage(method, uri);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", access.Token);
                return await (http ?? ProviderHttp).SendAsync(request, token);
            }

            using var current = await SendAsync(HttpMethod.Get, expected + "?api-version=7.5");
            if (current.StatusCode == HttpStatusCode.OK)
                return (true, "Confirmed exact retained destination key version.");
            if (current.StatusCode != HttpStatusCode.NotFound)
                return (false, "Destination key-version state is unconfirmed; no broad recovery.");
            var vault = new Uri(expected);
            var recovery = vault.GetLeftPart(UriPartial.Authority)
                + "/deletedsecrets/github-app-private-key/recover?api-version=7.5";
            using var restored = await SendAsync(HttpMethod.Post, recovery);
            if (restored.StatusCode is not (HttpStatusCode.OK or HttpStatusCode.Accepted))
                return (false, "Exact recoverable destination key restoration was denied or uncertain.");
            using var confirmed = await SendAsync(HttpMethod.Get, expected + "?api-version=7.5");
            return confirmed.StatusCode == HttpStatusCode.OK
                ? (true, "Recovered exact retained destination key version.")
                : (false, "Destination key recovery was not confirmed at the retained version URI.");
        }
        catch (Exception e) when (e is HttpRequestException or IOException or JsonException
            or FormatException or InvalidOperationException or KeyNotFoundException or OperationCanceledException)
        {
            if (e is OperationCanceledException && token.IsCancellationRequested) throw;
            return (false, "Exact destination key recovery outcome is unknown; inspect retained vault custody.");
        }
    }

    private static async Task InvalidateReadinessAsync(
        OwnerReservationStore product, FactoryReservationIdentity identity, CancellationToken token)
    {
        if (await product.ReadAsync(ProductConfigurationKeys.FactoryGitHubAuthority, token) is { } githubRecord)
        {
            var github = JsonSerializer.Deserialize<FactoryGitHubAuthority>(
                githubRecord.Value, FactoryGitHubJson.Options) ?? throw new JsonException();
            if (github.FactoryId != identity.FactoryId) throw new JsonException();
            await product.WriteAsync(githubRecord.Key, FactoryReservationRecords.Serialize(github with
            {
                State = GitHubAuthorityState.Partial,
                Stage = "host-verification",
                Proof = null,
                Detail = "Reattachment requires current App, installation, repository, token and workload verification.",
            }), githubRecord.ETag, token);
        }
        if (await product.ReadAsync(ProductConfigurationKeys.FactoryTelemetry, token) is { } telemetryRecord)
        {
            var telemetry = FactoryReservationRecords.Parse<FactoryTelemetry>(telemetryRecord.Value);
            await product.WriteAsync(telemetryRecord.Key, FactoryReservationRecords.Serialize(telemetry with
            {
                State = TelemetryState.Partial,
                Stage = "prepared",
                Proof = null,
                CheckId = null,
                LastCheckReference = null,
                LastCheckSha256 = null,
                VerifiedProofReference = null,
                VerifiedProofSha256 = null,
                Detail = "Reattachment requires current identity, network, schema, confinement and fresh application evidence.",
            }), telemetryRecord.ETag, token);
        }
        if (await product.ReadAsync(ProductConfigurationKeys.FactoryModels, token) is { } modelsRecord)
        {
            var models = FactoryReservationRecords.Parse<FactoryModels>(modelsRecord.Value);
            await product.WriteAsync(modelsRecord.Key, FactoryReservationRecords.Serialize(models with
            {
                State = ModelCapabilityState.Partial,
                Stage = "prepared",
                Proof = null,
                CheckId = null,
                LastProofReference = null,
                LastProofSha256 = null,
                VerifiedUntil = null,
                Detail = "Reattachment requires fresh synthesis, embedding and distinct-juror capability proof.",
            }), modelsRecord.ETag, token);
        }
        if (await product.ReadAsync(ProductConfigurationKeys.FactorySource, token) is { } sourceRecord)
        {
            var source = FactoryReservationRecords.Parse<FactorySource>(sourceRecord.Value);
            await product.WriteAsync(sourceRecord.Key, FactoryReservationRecords.Serialize(source with
            {
                State = SourceState.Partial,
                Stage = "prepared",
                Detail = "Reattachment requires current repository/App access and a newly confirmed complete snapshot.",
            }), sourceRecord.ETag, token);
        }
        if (await product.ReadAsync(ProductConfigurationKeys.FactorySupplemental, token) is { } supplementalRecord)
        {
            var supplemental = FactoryReservationRecords.Parse<FactorySupplemental>(supplementalRecord.Value);
            await product.WriteAsync(supplementalRecord.Key, FactoryReservationRecords.Serialize(supplemental with
            {
                State = supplemental.State == SupplementalState.NotSelected
                    ? SupplementalState.NotSelected : SupplementalState.Partial,
                Stage = "prepared",
                CheckId = null,
                Checks = [],
                Detail = supplemental.State == SupplementalState.NotSelected
                    ? supplemental.Detail
                    : "Reattachment requires renewed proof for every selected supplemental source.",
            }), supplementalRecord.ETag, token);
        }
    }

    private static async Task<(bool Matches, DateTimeOffset CreatedOn)> ReadGrantAsync(
        HttpResponseMessage response, FoundationOperation expected, CancellationToken token)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(token),
            new JsonDocumentOptions { AllowDuplicateProperties = false });
        var root = document.RootElement;
        var properties = root.GetProperty("properties");
        var matches = string.Equals(root.GetProperty("id").GetString()?.TrimEnd('/'), expected.Target.TrimEnd('/'),
                   StringComparison.OrdinalIgnoreCase)
               && properties.GetProperty("principalId").GetString() == expected.PrincipalId
               && string.Equals(properties.GetProperty("roleDefinitionId").GetString()?.TrimEnd('/'),
                   expected.RoleDefinitionId?.TrimEnd('/'), StringComparison.OrdinalIgnoreCase)
               && string.Equals(properties.GetProperty("scope").GetString()?.TrimEnd('/'),
                   expected.RoleScope?.TrimEnd('/'), StringComparison.OrdinalIgnoreCase)
               && Optional(properties, "condition") == expected.Condition
               && Optional(properties, "conditionVersion") == expected.ConditionVersion;
        return (matches, properties.GetProperty("createdOn").GetDateTimeOffset());
    }

    private static string? Optional(JsonElement properties, string name) =>
        !properties.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null
            ? null : value.GetString();

    private static FactoryReattachmentResult Result(FactoryReattachmentState state, string detail) => new(state, detail);
}
