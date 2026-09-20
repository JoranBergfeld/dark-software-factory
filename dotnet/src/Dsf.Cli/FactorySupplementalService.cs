using System.Text.Json;
using Dsf.Core.Onboarding;
using Dsf.Core.Runtime;

namespace Dsf.Cli;

internal sealed partial class FactorySupplementalService
{
    internal async Task<FactorySupplemental> ReviewChangeAsync(FactoryReservationResult admission, SupplementalSelection selection, CancellationToken token)
    {
        current = await PreviewAsync(admission, selection, token);
        effects.Clear();
        await CheckNativeAsync(admission, token);
        return current;
    }

    internal async Task<FactorySupplemental> PreviewAsync(FactoryReservationResult admission, SupplementalSelection selection, CancellationToken token)
    {
        var preview = Preview(admission, selection);
        await ReadGovernanceAsync(admission, Store(admission), token);
        var revisions = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var source in selection.Sources.Where(s => s.Kind == "foundryiq"))
        {
            using var native = await GetAsync(admission, source.SearchResourceId!, "2025-05-01", token);
            revisions.Add(source.SearchResourceId!, FactoryTelemetryValidation.Hash(native.RootElement));
        }
        var config = preview.Configuration! with { PrerequisiteRevisions = revisions };
        preview = preview with { Configuration = config, ApprovalFingerprint = FactoryTelemetryValidation.Hash(config) };
        CheckCapacity(preview, manifest);
        return preview;
    }

    internal static FactorySupplemental Preview(FactoryReservationResult admission, SupplementalSelection selection)
    {
        if (admission.Identity is not { } identity || admission.Foundation is not { State: FactoryFoundationState.Inactive } foundation
            || admission.Plan is not { } plan) throw new JsonException();
        FactorySupplementalValidation.ValidateSelection(selection, identity.SubscriptionId);
        var assets = new FactoryFoundationAssets(identity, plan.Selection);
        var hosting = Binding(assets.HostingEnvironment);
        var vault = selection.Sources.Any(s => s.Kind == "webiq")
            ? Binding(assets.Group + "/providers/microsoft.keyvault/vaults/" + assets.Prefix + "-vault") : null;
        var config = new SupplementalConfiguration(identity.FactoryId, identity.PlanFingerprint, foundation.ProfilePin!,
            foundation.ProductEndpoint!, assets.Group, plan.Selection.FactoryLocation, selection, hosting, vault)
        { TemplateSha256 = FactoryReservationRecords.Hash(File.ReadAllBytes(new ProvisioningAssets().Resolve("onboard-supplemental.json"))) };
        config = config with { ResourceApprovalSha256 = FactoryTelemetryValidation.Hash(FactorySupplementalValidation.Resources(config)) };
        var preview = new FactorySupplemental(SupplementalState.Partial,
            "Review exact opt-in: distinct new source identities/hosts and only approved data grants. WebIQ requires operator-seeded product-vault secret; DSF never obtains or seeds a key. " + FactorySupplementalValidation.Coverage)
        { Configuration = config, ApprovalFingerprint = FactoryTelemetryValidation.Hash(config) };
        OwnerReservationStore.CheckSize(ProductConfigurationKeys.FactorySupplemental, ProductConfigurationKeys.NoLabel, FactoryReservationRecords.Serialize(preview));
        return preview;

        FoundationOperation Binding(string target)
        {
            var binding = foundation.Operations.Single(o => o.Target == target);
            if (binding.Disposition != FoundationResourceDisposition.Created || binding.State != FoundationOperationState.Completed
                || binding.ResourceId != target || binding.Incarnation is null || binding.ProviderOperationId is null) throw new JsonException();
            return binding;
        }
    }
}
