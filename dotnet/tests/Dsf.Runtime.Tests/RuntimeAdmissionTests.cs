using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Dsf.Core.Onboarding;
using Dsf.Core.Runtime;
using Dsf.FeatureCouncil.Conveyor;
using Microsoft.AspNetCore.Builder;
using Xunit;
using static Dsf.Runtime.Tests.TestDependencies;

namespace Dsf.Runtime.Tests;

public sealed class RuntimeAdmissionTests
{
    [Theory]
    [InlineData("dsf/factory/offboarding")]
    [InlineData("dsf/factory/audit/offboarding/retained/request")]
    public void Retained_inspection_degrades_offboarding_to_closed_admission(string key)
    {
        var inspection = new FactoryInspectionAuthority();

        Assert.False(inspection.Include(key));
        Assert.True(inspection.AdmissionClosed);
    }

    private static readonly RuntimeSettings Settings = new(
        "acme", "https://dsfo-11111111111111111111111111111111.azconfig.io.", "", "",
        "https://cosmos.example", "https://openai.example", "gpt-deploy", "embed-deploy", "", "", "", "acme/acme");

    private static readonly IReadOnlyDictionary<string, string?> Confirmed = new Dictionary<string, string?>
    {
        [RuntimeIntegrationSettings.ConfirmLiveFiling] = "true",
        [RuntimeIntegrationSettings.ConfirmLiveOutcomes] = "true",
    };

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData(null)]
    public async Task Source_host_CLI_preserves_an_invalid_pin_instead_of_downgrading_to_legacy_registration(string? pin)
    {
        var source = new ObservedSource();
        var runner = new GatherHostRunner();
        var configuration = PersistedRuntimeConfiguration.Greenfield();
        var dependencies = TestDependencies.Build(sourceIntegration: source, webHostRunner: runner,
            configurationSettingsGateway: configuration);
        var env = new Dictionary<string, string?>
        {
            ["DSF_PRODUCT"] = "acme", ["AZURE_APPCONFIG_ENDPOINT"] = "https://appconfig.example",
            ["AZURE_COSMOS_ENDPOINT"] = "https://cosmos.example", ["AZURE_OPENAI_ENDPOINT"] = "https://openai.example",
            ["AZURE_OPENAI_DEPLOYMENT"] = "gpt", ["AZURE_OPENAI_EMBEDDING_DEPLOYMENT"] = "embedding",
            [RuntimeIntegrationSettings.FactoryProfilePin] = pin,
        };
        using var output = new StringWriter();
        using var error = new StringWriter();

        await RuntimeCliApplication.InvokeAsync(["serve-agent", "--kind", "azuremonitor", "--host", "127.0.0.1", "--port", "0"],
            env, output, error, dependencies, CancellationToken.None);

        Assert.Equal(HttpStatusCode.Forbidden, runner.Status);
        Assert.Equal(0, source.Gathers);
        Assert.Empty(configuration.Writes);
    }

    public static IEnumerable<object[]> AdmissionCases()
    {
        foreach (var entry in new[] { "manual", "preview", "resume", "sweep", "outcomes", "http", "agent", "loop" })
        foreach (var damage in new[] { "none", "invalid-pin", "missing-profile", "malformed-profile", "unreadable",
                     "missing-manifest", "missing-all", "mutating-profile", "active-manifest", "wrong-factory", "legacy-coexists",
                     "pause", "pause-history", "offboarding", "offboarding-history", "reattachment-awaiting-readiness" })
            yield return [entry, damage];
    }

    [Theory]
    [MemberData(nameof(AdmissionCases))]
    public async Task Current_onboarding_governance_denies_every_runtime_path_without_effects(string entry, string damage)
    {
        var configuration = damage == "reattachment-awaiting-readiness"
            ? PersistedRuntimeConfiguration.Greenfield()
            : Onboarding();
        var settings = Settings;
        if (damage == "invalid-pin")
            settings = settings with { IntegrationSettings = new Dictionary<string, string?> { [RuntimeIntegrationSettings.FactoryProfilePin] = "invalid" } };
        if (damage is "missing-profile" or "legacy-coexists") configuration.Values.Remove(ProductConfigurationKeys.FactoryProfile);
        if (damage == "malformed-profile") configuration.Values[ProductConfigurationKeys.FactoryProfile] = "{secret-governance-provider-detail";
        if (damage == "missing-manifest") configuration.Values.Remove(ProductConfigurationKeys.FactoryManifest);
        if (damage == "missing-all") configuration.Values.Clear();
        if (damage == "unreadable") configuration.ReadFailure = new Azure.RequestFailedException(403, "secret-governance-provider-detail");
        if (damage == "pause") configuration.Values.Add("dsf/factory/pause", "{unconfirmed-private-stop");
        if (damage == "pause-history") configuration.Values.Add("dsf/factory/audit/pause/retained/request", "{}");
        if (damage == "offboarding") configuration.Values.Add("dsf/factory/offboarding", "{unconfirmed-private-detach");
        if (damage == "offboarding-history") configuration.Values.Add("dsf/factory/audit/offboarding/retained/request", "{}");
        if (damage == "reattachment-awaiting-readiness")
            configuration.Values.Add(FactoryReattachment.Key, JsonSerializer.Serialize(
                new FactoryReattachmentRequest(1, FactoryReattachment.Kind, Guid.NewGuid(),
                    Guid.Parse("11111111-1111-1111-1111-111111111111"), new string('a', 64),
                    new string('b', 64), "55555555-5555-5555-5555-555555555555",
                    DateTimeOffset.Parse("2026-09-15T23:00:00Z")),
                FactorySourceValidation.RecordJson));
        if (damage == "legacy-coexists")
            foreach (var item in PersistedRuntimeConfiguration.Greenfield().Values) configuration.Values.Add(item.Key, item.Value);
        if (damage is "mutating-profile" or "active-manifest" or "wrong-factory")
        {
            var manifest = JsonNode.Parse(configuration.Values[ProductConfigurationKeys.FactoryManifest])!;
            if (damage == "mutating-profile")
            {
                var profile = JsonNode.Parse(configuration.Values[ProductConfigurationKeys.FactoryProfile])!;
                profile["allowedRepositoryMutations"] = new JsonArray("create-pull-request");
                configuration.Values[ProductConfigurationKeys.FactoryProfile] = profile.ToJsonString();
                manifest["profilePin"] = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(profile.ToJsonString())));
            }
            if (damage == "active-manifest") manifest["publication"] = "Active";
            if (damage == "wrong-factory") manifest["identity"]!["factoryId"] = "99999999-9999-9999-9999-999999999999";
            configuration.Values[ProductConfigurationKeys.FactoryManifest] = manifest.ToJsonString();
        }
        var before = JsonSerializer.Serialize(configuration.Values);
        var persisted = new ConveyorRun
        {
            Id = RunIdentity.Compute(TriggerKind.Signal, ["acme"], ["azuremonitor"]),
            ProductHints = ["acme"], SourceKinds = ["azuremonitor"], DryRun = true,
        };
        if (entry == "resume") persisted.Checkpoints.AddRange(ConveyorLine.StationNames.Take(6));
        var store = new SerializedAdmissionRunStore(JsonSerializer.Serialize(persisted));
        var originalRun = store.Payload;
        var source = new ObservedSource();
        var filer = new RecordingIssueFiler();
        var model = new RecordingModelClient();
        var tracer = new RecordingTracer();
        var lease = new ScriptedSweepLease();
        var outcomes = new RecordingOutcomeSource();
        var learning = new RecordingLearningStore();
        var dependencies = TestDependencies.Build(configurationSettingsGateway: configuration, sourceIntegration: source,
            runStore: store, issueFiler: filer, modelClient: model, tracer: tracer, sweepLeaseFactory: _ => lease,
            learningComposer: new ScriptedLearningComposer(outcomes, learning),
            evidenceGatherers: [new ScriptedEvidenceGatherer("azuremonitor", new EvidenceItem("azuremonitor", "incident-1", "checkout failures"))]);
        string error;
        if (entry is "agent" or "http")
        {
            await using var host = entry == "agent"
                ? RuntimeVerbs.BuildSourceAgentHost(settings, "azuremonitor", dependencies, "127.0.0.1", 0)
                : RuntimeVerbs.BuildOrchestratorHost(settings, dependencies, "127.0.0.1", 0, env: Confirmed);
            await host.StartAsync();
            try
            {
                using var client = new HttpClient { BaseAddress = new Uri(host.Urls.Single()) };
                using var response = await client.PostAsync(entry == "agent" ? SourceAgentCard.GatherRoute : "/run",
                    new StringContent("""{"product_hints":"acme","source_kinds":["azuremonitor"]}""", Encoding.UTF8, "application/json"));
                Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
                error = await response.Content.ReadAsStringAsync();
            }
            finally { await host.StopAsync(); }
        }
        else
        {
            var denied = await Assert.ThrowsAsync<RuntimeVerbException>(async () =>
            {
                if (entry == "sweep") await RuntimeVerbs.SweepAsync(settings, false, dependencies, CancellationToken.None, Confirmed);
                else if (entry == "outcomes") await RuntimeVerbs.PollOutcomesAsync(settings, false, true, Confirmed, dependencies, CancellationToken.None);
                else if (entry == "loop") await RuntimeVerbs.ServeOrchestratorAsync(settings, dependencies, "127.0.0.1", 0,
                    TimeSpan.FromMilliseconds(10), CancellationToken.None, Confirmed);
                else await RuntimeVerbs.RunSignalAsync(settings,
                    SignalReader.ReadFromJson("""{"product_hints":"acme","source_kinds":["azuremonitor"]}""", "test", entry == "preview"),
                    dependencies, CancellationToken.None, Confirmed);
            });
            error = denied.Message;
        }
        Assert.Contains(damage is "pause" or "pause-history" ? "DeliberatePause"
            : damage is "offboarding" or "offboarding-history" ? "OffboardingOrDetached"
            : damage == "reattachment-awaiting-readiness" ? "ReattachmentAwaitingReadiness"
            : damage == "none" ? "Verified product governance"
            : damage == "unreadable" ? "unreadable" : "governance", error);
        Assert.DoesNotContain("secret-governance-provider-detail", error);
        Assert.Single(configuration.Reads);
        Assert.Empty(configuration.Writes);
        Assert.Equal(before, JsonSerializer.Serialize(configuration.Values));
        Assert.Equal(0, source.Gathers);
        Assert.Equal(0, store.Writes);
        Assert.Equal(originalRun, store.Payload);
        Assert.Empty(filer.Filed);
        Assert.Empty(model.Prompts);
        Assert.Empty(tracer.Traced);
        Assert.Empty(lease.Requests);
        Assert.Equal(0, outcomes.PollCount);
        Assert.Empty(learning.Recorded);
    }

    [Theory]
    [InlineData("https://dsfo-11111111111111111111111111111111.azconfig.io")]
    [InlineData("https://DSFO-11111111111111111111111111111111.AZCONFIG.IO.")]
    [InlineData("https://dsfo-11111111111111111111111111111111.azconfig.io.:443/")]
    public async Task Equivalent_endpoint_spellings_resolve_the_same_authenticated_inactive_governance(string endpoint)
    {
        var configuration = Onboarding();
        var dependencies = TestDependencies.Build(configurationSettingsGateway: configuration);
        var error = await Assert.ThrowsAsync<RuntimeVerbException>(() => RuntimeVerbs.RunSignalAsync(
            Settings with { AppConfigEndpoint = endpoint },
            SignalReader.ReadFromJson("""{"product_hints":"acme","source_kinds":["azuremonitor"]}""", "test", false),
            dependencies, CancellationToken.None, Confirmed));
        Assert.Contains("Verified product governance", error.Message);
        Assert.Equal("https://dsfo-11111111111111111111111111111111.azconfig.io", Assert.Single(configuration.Reads));
        Assert.Empty(configuration.Writes);
    }

    [Fact]
    public async Task A_live_host_rechecks_current_authority_instead_of_caching_prior_greenfield_admission()
    {
        var configuration = PersistedRuntimeConfiguration.Greenfield();
        var source = new ObservedSource();
        var dependencies = TestDependencies.Build(configurationSettingsGateway: configuration, sourceIntegration: source);
        await using var host = RuntimeVerbs.BuildSourceAgentHost(Settings with { AppConfigEndpoint = "https://appconfig.example" },
            "azuremonitor", dependencies, "127.0.0.1", 0);
        await host.StartAsync();
        try
        {
            using var client = new HttpClient { BaseAddress = new Uri(host.Urls.Single()) };
            using var admitted = await client.PostAsync(SourceAgentCard.GatherRoute, null);
            Assert.Equal(HttpStatusCode.OK, admitted.StatusCode);
            configuration.Values.Clear();
            using var denied = await client.PostAsync(SourceAgentCard.GatherRoute, null);
            Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
            Assert.Equal(1, source.Gathers);
            Assert.Equal(2, configuration.Reads.Count);
            Assert.Empty(configuration.Writes);
        }
        finally { await host.StopAsync(); }
    }

    [Fact]
    public async Task A_present_but_null_pin_is_invalid_not_verified_inactive_governance()
    {
        var configuration = Onboarding();
        var dependencies = TestDependencies.Build(configurationSettingsGateway: configuration);
        var settings = Settings with
        {
            IntegrationSettings = new Dictionary<string, string?> { [RuntimeIntegrationSettings.FactoryProfilePin] = null },
        };

        var error = await Assert.ThrowsAsync<RuntimeVerbException>(() => RuntimeVerbs.RunSignalAsync(
            settings, SignalReader.ReadFromJson("""{"product_hints":"acme","source_kinds":["azuremonitor"]}""", "test", false),
            dependencies, CancellationToken.None, Confirmed));

        Assert.Contains("inconsistent", error.Message);
        Assert.Single(configuration.Reads);
        Assert.Empty(configuration.Writes);
    }

    [Fact]
    public async Task An_unrequested_governance_read_timeout_is_a_redacted_admission_failure_not_a_cancellation()
    {
        var configuration = Onboarding();
        configuration.ReadFailure = new TaskCanceledException("secret-governance-provider-detail");
        var source = new ObservedSource();
        var dependencies = TestDependencies.Build(sourceIntegration: source, configurationSettingsGateway: configuration);

        var error = await Assert.ThrowsAsync<RuntimeVerbException>(() => RuntimeVerbs.RunSignalAsync(
            Settings, SignalReader.ReadFromJson("""{"product_hints":"acme","source_kinds":["azuremonitor"]}""", "test", false),
            dependencies, CancellationToken.None, Confirmed));

        Assert.Contains("unreadable", error.Message);
        Assert.DoesNotContain("secret-governance-provider-detail", error.Message);
        Assert.Equal(0, source.Gathers);
        Assert.Empty(configuration.Writes);
    }

    [Theory]
    [InlineData("dsf/factory/profile")]
    [InlineData("dsf/factory/manifest")]
    public async Task Null_SDK_governance_values_are_explicit_admission_failures(string key)
    {
        var configuration = Onboarding();
        configuration.Values[key] = JsonSerializer.Deserialize<string>("null")!;
        var model = new RecordingModelClient();
        var dependencies = TestDependencies.Build(configurationSettingsGateway: configuration, modelClient: model);

        var error = await Assert.ThrowsAsync<RuntimeVerbException>(() => RuntimeVerbs.RunSignalAsync(
            Settings, SignalReader.ReadFromJson("""{"product_hints":"acme","source_kinds":["azuremonitor"]}""", "test", false),
            dependencies, CancellationToken.None, Confirmed));

        Assert.Contains("admission denied", error.Message);
        Assert.Empty(model.Prompts);
        Assert.Empty(configuration.Writes);
    }

    internal static PersistedRuntimeConfiguration Onboarding()
    {
        var configuration = new PersistedRuntimeConfiguration();
        var identity = new FactoryReservationIdentity(Guid.Parse("11111111-1111-1111-1111-111111111111"),
            Guid.Parse("22222222-2222-2222-2222-222222222222"), "https://owner.azconfig.io", "acme", 913,
            "33333333-3333-3333-3333-333333333333", "44444444-4444-4444-4444-444444444444", "production",
            new string('a', 64), new string('b', 64), "55555555-5555-5555-5555-555555555555", 17,
            DateTimeOffset.Parse("2026-09-15T23:00:00Z"));
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var profile = JsonSerializer.Serialize(new FactoryOnboardingProfile(
            1, "onboard-decide-inactive-v1", identity.FactoryId, identity.PlanFingerprint, []), options);
        configuration.Values[ProductConfigurationKeys.FactoryProfile] = profile;
        configuration.Values[ProductConfigurationKeys.FactoryManifest] = JsonSerializer.Serialize(
            new FactoryManifest(1, "decide-factory-manifest", "product-app-configuration", "Inactive",
                identity, new string('c', 64), 4000, 1, Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(profile)))), options);
        return configuration;
    }

    [Theory]
    [InlineData("https://dsfo-11111111111111111111111111111111.azconfig.io.")]
    [InlineData("https://unregistered.example")]
    public async Task Source_gather_without_current_registration_is_denied_even_without_pin_or_canonical_spelling(string endpoint)
    {
        var configuration = new PersistedRuntimeConfiguration();
        var source = new ObservedSource();
        var dependencies = TestDependencies.Build(sourceIntegration: source, configurationSettingsGateway: configuration);
        await using var host = RuntimeVerbs.BuildSourceAgentHost(Settings with { AppConfigEndpoint = endpoint }, "azuremonitor", dependencies, "127.0.0.1", 0);
        await host.StartAsync();
        try
        {
            using var client = new HttpClient { BaseAddress = new Uri(host.Urls.Single()) };
            using var response = await client.PostAsync(SourceAgentCard.GatherRoute, null);

            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            Assert.Equal(0, source.Gathers);
            Assert.Single(configuration.Reads);
            Assert.Empty(configuration.Writes);
        }
        finally { await host.StopAsync(); }
    }

    [Theory]
    [InlineData("{\"routing\":null}")]
    [InlineData("{\"routing\":[null]}")]
    [InlineData("{\"routing\":[],\"routing\":[\"feature-council\"]}")]
    [InlineData(null)]
    public async Task Malformed_legacy_registration_is_not_a_greenfield_compatibility_grant(string? taxonomy)
    {
        var configuration = PersistedRuntimeConfiguration.Greenfield();
        configuration.Values[ProductConfigurationKeys.LabelTaxonomy] = taxonomy!;
        var source = new ObservedSource();
        var dependencies = TestDependencies.Build(configurationSettingsGateway: configuration, sourceIntegration: source);
        await using var host = RuntimeVerbs.BuildSourceAgentHost(Settings with { AppConfigEndpoint = "https://appconfig.example" },
            "azuremonitor", dependencies, "127.0.0.1", 0);
        await host.StartAsync();
        try
        {
            using var client = new HttpClient { BaseAddress = new Uri(host.Urls.Single()) };
            using var response = await client.PostAsync(SourceAgentCard.GatherRoute, null);
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            Assert.Equal(0, source.Gathers);
            Assert.Empty(configuration.Writes);
        }
        finally { await host.StopAsync(); }
    }

    private sealed class ObservedSource : ISourceIntegration
    {
        public int Gathers { get; private set; }
        public Task<IReadOnlyList<EvidenceItem>> GatherAsync(string kind, string product, CancellationToken cancellationToken)
        {
            Gathers++;
            return Task.FromResult<IReadOnlyList<EvidenceItem>>([]);
        }
    }

    private sealed class GatherHostRunner : IWebHostRunner
    {
        public HttpStatusCode? Status { get; private set; }
        public async Task RunAsync(WebApplication app, CancellationToken cancellationToken)
        {
            await using var host = app;
            await host.StartAsync(cancellationToken);
            try
            {
                using var client = new HttpClient { BaseAddress = new Uri(host.Urls.Single()) };
                using var response = await client.PostAsync(SourceAgentCard.GatherRoute, null, cancellationToken);
                Status = response.StatusCode;
            }
            finally { await host.StopAsync(CancellationToken.None); }
        }
    }
}
