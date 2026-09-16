using System.Globalization;
using Dsf.Core.Onboarding;
using Dsf.Core.Runtime;

namespace Dsf.Cli;

internal sealed partial class FactoryTelemetryService
{
    private static void CheckCapacity(FactoryTelemetry preview)
    {
        var config = preview.Configuration;
        var count = config.AccessGrants.Count + 2;
        if (count > 64)
            throw new ReservationStoreException(FactoryReservationState.Failed, "Telemetry lifecycle exceeds the supported operation count; no preparation intent permitted.");

        // Capacity-only upper envelopes, never persisted, returned as proof, or sent to a provider.
        var id = Guid.Empty;
        var time = DateTimeOffset.MaxValue;
        var hash = new string('f', 64);
        var detail = new string('x', FactoryTelemetryLimits.DetailJsonBytes);
        var records = config.Backends.SelectMany(b => b.Tables.Select(t => new TelemetryRecordReference(
            b.ResourceId, b.WorkspaceId, t, b.ApplicationResourceIds.MaxBy(FactoryTelemetryLimits.StringJsonBytes)!,
            time, new string('\uffff', FactoryTelemetryLimits.ItemIdCharacters), 499999))).ToArray();
        var proof = new TelemetryProof(Longest<TelemetryState>(), Longest<TelemetryReason>(), config.FactoryId, id,
            preview.ApprovalFingerprint, config.SourceIdentity.PrincipalId!, config.ApplicationEnvironment,
            time, time, time, records, detail)
        {
            CheckId = id,
            Queries = records.Select(r => new TelemetryQueryObservation(r.BackendResourceId, r.Table, Longest<TelemetryReason>(),
                hash, id.ToString(), 499999, r)).ToArray(),
        };
        var proofKey = ProductConfigurationKeys.TelemetryProof(id, id);
        Check(proofKey, proof);
        var envelope = preview with
        {
            OperationId = id, State = Longest<TelemetryState>(), Stage = "preparation", Detail = detail, Proof = null,
            LastFailure = Longest<TelemetryReason>(), CheckId = id, OperationCount = count,
            HostEndpoint = new string('x', FactoryTelemetryLimits.HostEndpointCharacters),
            EffectHashes = Enumerable.Range(0, count).ToDictionary(i => i.ToString(CultureInfo.InvariantCulture), _ => hash),
            LastCheckReference = proofKey, LastCheckSha256 = hash, VerifiedProofReference = proofKey, VerifiedProofSha256 = hash,
        };
        Check(ProductConfigurationKeys.FactoryTelemetry, envelope);
        var host = FactoryTelemetryValidation.HostTarget(config);
        var group = config.HostingEnvironment.Target[..config.HostingEnvironment.Target.IndexOf("/providers/", StringComparison.Ordinal)];
        for (var i = 0; i < count; i++)
        {
            var grant = i < config.AccessGrants.Count ? config.AccessGrants[i] : null;
            var target = grant?.Target ?? (i == count - 2 ? host : host + "/authconfigs/current");
            var receipt = new FoundationOperation(1, OperationKind, config.FactoryId, id, i, target,
                grant is null ? "2025-01-01" : "2022-04-01", hash, id, time,
                Longest<FoundationOperationState>(), Longest<FoundationResourceDisposition>())
            {
                HttpStatus = 599, ProviderRequestId = id.ToString(), ProviderCorrelationId = id.ToString(),
                ResponseDate = time, ResourceId = target, Incarnation = time.ToString("O", CultureInfo.InvariantCulture),
                ProviderOperationId = new string('f', FactoryTelemetryLimits.ProviderOperationIdCharacters),
                DeploymentTarget = grant is null ? group + "/providers/microsoft.resources/deployments/tm-" + id.ToString("N") : null,
                PrincipalId = grant?.PrincipalId, RoleScope = grant?.Scope, RoleDefinitionId = grant?.RoleDefinitionId,
                LogicalIdentity = grant is null ? null : "factory:source-azuremonitor", ConditionVersion = "2.0",
            };
            Check(ProductConfigurationKeys.TelemetryOperation(id, i), receipt);
        }

        static T Longest<T>() where T : struct, Enum =>
            Enum.GetValues<T>().MaxBy(value => FactoryReservationRecords.Serialize(value).Length);
        static void Check<T>(string key, T value)
        {
            var bytes = OwnerReservationStore.RecordSizeBytes(key, ProductConfigurationKeys.NoLabel, FactoryReservationRecords.Serialize(value));
            if (bytes > OwnerReservationStore.RecordLimit)
                throw new ReservationStoreException(FactoryReservationState.Failed,
                    $"Telemetry lifecycle record {key} requires {bytes} bytes including metadata; maximum is {OwnerReservationStore.RecordLimit}. "
                    + "Selection rejected before preparation intent. Review a smaller selection; no evidence or references truncated.");
        }
    }
}
