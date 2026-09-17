using System.Text.Json;
using Dsf.Core.Onboarding;
using Dsf.Core.Runtime;
using Dsf.FeatureCouncil.Conveyor;

namespace Dsf.Runtime;

/// <summary>
/// Runtime workflow invariants shared by CLI and HTTP adapters: live-filing
/// gates, stable run identity, resume rules, and pre-station load failures.
/// </summary>
internal static partial class RuntimeWorkflow
{
    internal static async Task<SourceCharterContextResult> ReadSourceCharterContextAsync(
        FactorySourceAccess source, IFactoryCharterEvidenceReader records, HttpClient http, TimeProvider clock, CancellationToken token)
    {
        var retained = await FactoryCharterAdoptionReader.ReadAsync(source.Manifest.Identity.FactoryId, records, token);
        if (retained is null)
            return new(FactoryCharterState.Blocked, "Explicit adopted charter required; draft readiness is not adoption.", null, null);
        var workflow = new FactoryIntentWorkflow(source, http, clock, charterReview: true);
        var analysis = await workflow.ReviewCharterAsync(token);
        if (analysis.State != IntentState.DraftReady) throw new IntentException(analysis.State, analysis.Detail);
        var charter = await workflow.ReadReviewedCharterAsync(token);
        FactoryCharterAdoptionReader.RequireCurrent(retained.Adoption, new(analysis, charter), source.Manifest.Identity.ProductKey);
        var current = await FactoryCharterAdoptionReader.ReadAsync(source.Manifest.Identity.FactoryId, records, token);
        if (current is null || current.Revision != retained.Revision || current.OperationRevision != retained.OperationRevision
            || FactoryModelsValidation.Hash(current.Adoption) != FactoryModelsValidation.Hash(retained.Adoption))
            throw new CharterAdoptionEvidenceException("Adoption changed while reading current source.");
        analysis = await workflow.RevalidateCharterReviewAsync(analysis, token);
        var evidence = new CharterEvidence(analysis, charter);
        FactoryCharterAdoptionReader.RequireCurrent(retained.Adoption, evidence, source.Manifest.Identity.ProductKey);
        current = await FactoryCharterAdoptionReader.ReadAsync(source.Manifest.Identity.FactoryId, records, token);
        if (current is null || current.Revision != retained.Revision || current.OperationRevision != retained.OperationRevision
            || FactoryModelsValidation.Hash(current.Adoption) != FactoryModelsValidation.Hash(retained.Adoption))
            throw new CharterAdoptionEvidenceException("Adoption changed during final native verification.");
        return new(FactoryCharterState.Adopted, "Current source-side adopted intent only; not council readiness or activation.", retained, evidence);
    }

    internal static Task<SupplementalEvidence> ObserveSupplementalAsync(IFactorySupplementalEvidenceSource source,
        SupplementalProofRequest request, CancellationToken token) => source.ReadAsync(request, token);

    internal static async Task<CharterEvidence> ReviewCharterAsync(FactorySourceAccess source, HttpClient http,
        TimeProvider clock, CancellationToken token)
    {
        var workflow = new FactoryIntentWorkflow(source, http, clock, charterReview: true);
        var analysis = await workflow.ReviewCharterAsync(token);
        if (analysis.State != IntentState.DraftReady) throw new IntentException(analysis.State, analysis.Detail);
        var charter = await workflow.ReadReviewedCharterAsync(token);
        analysis = await workflow.RevalidateCharterReviewAsync(analysis, token);
        return new(analysis, charter);
    }

    internal static Task<IntentResult> InferCharterAsync(FactorySourceAccess source, HttpClient http, TimeProvider clock,
        IntentRequest request, bool status, CancellationToken token) =>
        new FactoryIntentWorkflow(source, http, clock).RunAsync(request, status, token);

    private static readonly JsonSerializerOptions RegistrationJson = new() { AllowDuplicateProperties = false };

    public static async Task EnsureWorkAdmittedAsync(
        RuntimeSettings settings, RuntimeDependencies dependencies, CancellationToken cancellationToken,
        IReadOnlyDictionary<string, string?>? env = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        string endpoint;
        try
        {
            endpoint = FactoryGovernance.NormalizeEndpoint(settings.AppConfigEndpoint);
            await foreach (var (key, value) in dependencies.ConfigurationSettingsGateway.ListAsync(
                endpoint, ProductConfigurationKeys.NoLabel, cancellationToken))
                if (!values.TryAdd(key, value)) throw new JsonException();
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            throw new RuntimeVerbException(
                "Runtime admission denied: required product registration/profile governance is unreadable or invalid. Provider details withheld.");
        }
        var configuredPin = settings.IntegrationSettings.GetValueOrDefault(RuntimeIntegrationSettings.FactoryProfilePin);
        var hasPin = settings.IntegrationSettings.ContainsKey(RuntimeIntegrationSettings.FactoryProfilePin)
            || env?.ContainsKey(RuntimeIntegrationSettings.FactoryProfilePin) == true;
        var envPin = env?.GetValueOrDefault(RuntimeIntegrationSettings.FactoryProfilePin);
        if (values.Keys.Any(key => key.StartsWith("dsf/factory/", StringComparison.Ordinal)))
        {
            try
            {
                var manifest = FactoryGovernance.ReadInactiveManifest(
                    values[ProductConfigurationKeys.FactoryManifest], values[ProductConfigurationKeys.FactoryProfile],
                    settings.Product, endpoint, configuredPin);
                if (settings.IntegrationSettings.ContainsKey(RuntimeIntegrationSettings.FactoryProfilePin) && configuredPin != manifest.ProfilePin
                    || env?.ContainsKey(RuntimeIntegrationSettings.FactoryProfilePin) == true && envPin != manifest.ProfilePin)
                    throw new JsonException();
            }
            catch (Exception exception) when (exception is JsonException or KeyNotFoundException or InvalidOperationException)
            {
                throw new RuntimeVerbException(
                    "Onboarding admission denied: required product manifest/profile governance is missing, invalid or inconsistent. No legacy fallback.");
            }
            throw new RuntimeVerbException("Onboarding factory is inactive. Verified product governance denies runtime work, including dry-run side effects.");
        }
        // Hints can only remove permissions. A successful authenticated legacy registration read is still required.
        var host = new Uri(endpoint).Host;
        if (hasPin || host.Length == 49 && host.StartsWith("dsfo-", StringComparison.Ordinal)
            && host.EndsWith(".azconfig.io", StringComparison.Ordinal) && Guid.TryParseExact(host.AsSpan(5, 32), "N", out _))
            throw new RuntimeVerbException("Onboarding factory is inactive: required product manifest/profile governance is missing. No legacy fallback.");
        try
        {
            var repository = Read<string>(ProductConfigurationKeys.GitHubRepository);
            if (repository.Split('/') is not [ { Length: > 0 }, { Length: > 0 } ]
                || !string.IsNullOrEmpty(settings.GitHubRepository)
                    && !repository.Equals(settings.GitHubRepository, StringComparison.OrdinalIgnoreCase))
                throw new JsonException();
            var taxonomy = Read<Dictionary<string, string?[]?>>(ProductConfigurationKeys.LabelTaxonomy);
            if (taxonomy.Any(pair => string.IsNullOrWhiteSpace(pair.Key)
                || pair.Value is null || pair.Value.Any(string.IsNullOrWhiteSpace)))
                throw new JsonException();
            _ = Read<string>(ProductConfigurationKeys.FoundryIqScope);
            if (Read<string?[]>(ProductConfigurationKeys.SentryProjects).Any(string.IsNullOrWhiteSpace)
                || Read<string?[]>(ProductConfigurationKeys.GrafanaDashboards).Any(string.IsNullOrWhiteSpace))
                throw new JsonException();
            _ = Read<string>(ProductConfigurationKeys.AzureMonitorScope);
            var threshold = Read<double>(ProductConfigurationKeys.Threshold(settings.Product));
            if (!double.IsFinite(threshold) || threshold is < 0 or > 1) throw new JsonException();
        }
        catch (Exception exception) when (exception is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            throw new RuntimeVerbException(
                "Runtime admission denied: no complete trusted legacy product registration or onboarding governance. No default greenfield permissions.");
        }

        T Read<T>(string key) => string.IsNullOrWhiteSpace(values[key]) ? throw new JsonException()
            : JsonSerializer.Deserialize<T>(values[key], RegistrationJson) ?? throw new JsonException();
    }

    public static void EnsureLiveFilingConfirmed(bool dryRun, IReadOnlyDictionary<string, string?>? env)
    {
        if (dryRun)
        {
            return;
        }

        var confirmed = (env is not null
            && env.TryGetValue(RuntimeIntegrationSettings.ConfirmLiveFiling, out var value)
                ? value
                : null)
            ?.Trim();
        if (!string.Equals(confirmed, "true", StringComparison.OrdinalIgnoreCase))
        {
            throw new RuntimeVerbException(
                "refusing to file live without an explicit manual gate: set "
                + $"{RuntimeIntegrationSettings.ConfirmLiveFiling}=true to confirm this run may file real GitHub "
                + "issues instead of previewing them.");
        }
    }

    public static async Task<ConveyorRun> LoadOrCreateRunAsync(
        ConveyorServices services,
        TriggerKind trigger,
        IReadOnlyList<string> productHints,
        IReadOnlyList<string> sourceKinds,
        bool dryRun,
        CancellationToken cancellationToken,
        bool resumeTerminal = true)
    {
        var runId = RunIdentity.Compute(trigger, productHints, sourceKinds);
        var run = new ConveyorRun
        {
            Id = runId,
            Trigger = trigger,
            ProductHints = productHints,
            SourceKinds = sourceKinds,
            DryRun = dryRun,
        };

        try
        {
            var existing = await services.RunStore.LoadAsync(runId, cancellationToken);
            if (existing is not null)
            {
                if (existing.Status != RunStatus.Open && !resumeTerminal)
                {
                    var fresh = new ConveyorRun
                    {
                        Id = Guid.NewGuid().ToString("n"),
                        Trigger = trigger,
                        ProductHints = productHints,
                        SourceKinds = sourceKinds,
                        DryRun = dryRun,
                    };
                    fresh.Record(
                        "run:load",
                        $"prior run '{existing.Id}' for this scope already reached terminal status "
                        + $"'{existing.Status}': starting a new run '{fresh.Id}' for this sweep instead of "
                        + "resuming it.");
                    return fresh;
                }

                if (existing.Status == RunStatus.Open && dryRun != existing.DryRun)
                {
                    existing.DryRun = dryRun;
                    existing.Record(
                        "run:load",
                        dryRun
                            ? "resumed under --dry-run: forcing this run to dry-run so it cannot file for real "
                              + "off checkpoints written by a prior non-dry-run invocation."
                            : "resumed without --dry-run: clearing this run's stale dry-run flag so it can file "
                              + "for real off checkpoints written by a prior --dry-run invocation.");
                }

                return existing;
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            run.Status = RunStatus.Error;
            run.FailureReason =
                $"could not resolve a prior run for identity '{runId}' ({exception.GetType().Name}): "
                + exception.Message;
            run.Record(
                "run:load", $"could not load a persisted run ({exception.GetType().Name}): {exception.Message}");
        }

        return run;
    }
}
