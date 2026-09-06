using Dsf.Core.Runtime;
using Dsf.FeatureCouncil.Conveyor;
using Dsf.FeatureCouncil.Conveyor.Stations;

namespace Dsf.Runtime.Tests;

/// <summary>
/// Test-only <see cref="RuntimeDependencies"/> builders and doubles. Production
/// runtime source ships only real adapters (ADR 0014); every deterministic double
/// the runtime tests need lives here, in the test project.
/// </summary>
internal static class TestDependencies
{
    /// <summary>Dependencies with no source agents and no issue filer wired.</summary>
    public static RuntimeDependencies Empty { get; } = Build();

    public static RuntimeDependencies Build(
        IOwnerRuntimeIndexReader? ownerRuntimeIndexReader = null,
        ISourceAgentRosterReader? sourceAgentRosterReader = null,
        IWebHostRunner? webHostRunner = null,
        IReadOnlyList<IEvidenceGatherer>? evidenceGatherers = null,
        IIssueFiler? issueFiler = null,
        IRunStore? runStore = null,
        ISourceIntegration? sourceIntegration = null,
        IReadOnlyDictionary<string, ISourceIntegration>? sourceIntegrationsByKind = null,
        IModelClient? modelClient = null,
        ITracer? tracer = null,
        ILearningComposer? learningComposer = null,
        Func<RuntimeSettings, ISweepControlStore>? sweepControlStoreFactory = null) =>
        new(
            ownerRuntimeIndexReader ?? new RecordingOwnerRuntimeIndexReader(),
            sourceAgentRosterReader ?? new RosterReader([]),
            webHostRunner ?? new RecordingWebHostRunner(),
            new ScriptedConveyorComposer(
                evidenceGatherers ?? [],
                issueFiler,
                runStore ?? new RecordingRunStore(),
                modelClient ?? new RecordingModelClient(),
                tracer ?? new RecordingTracer()),
            new SourceIntegrationRegistry(
                sourceIntegrationsByKind ?? new Dictionary<string, ISourceIntegration>(StringComparer.Ordinal),
                sourceIntegration ?? new ScriptedSourceIntegration()),
            learningComposer ?? new ScriptedLearningComposer(new RecordingOutcomeSource(), new RecordingLearningStore()),
            sweepControlStoreFactory);
}

/// <summary>Composes learning services from collaborators the test supplied directly.</summary>
internal sealed class ScriptedLearningComposer(IOutcomeSource outcomeSource, ILearningStore learningStore)
    : ILearningComposer
{
    public LearningServices ComposeFor(RuntimeSettings settings) => new(outcomeSource, learningStore);
}

/// <summary>A learning composer that always reports missing settings, for gate/failure tests.</summary>
internal sealed class UnconfiguredLearningComposer(string reason, IReadOnlyList<string> missing) : ILearningComposer
{
    public LearningServices ComposeFor(RuntimeSettings settings) =>
        throw new RuntimeConfigurationException(reason, missing);
}

/// <summary>An outcome source that answers a fixed, scripted list of signals.</summary>
internal sealed class RecordingOutcomeSource(params OutcomeSignal[] signals) : IOutcomeSource
{
    public int PollCount { get; private set; }

    public Task<IReadOnlyList<OutcomeSignal>> PollAsync(CancellationToken cancellationToken)
    {
        PollCount++;
        return Task.FromResult<IReadOnlyList<OutcomeSignal>>(signals);
    }
}

/// <summary>An outcome source whose backend cannot be reached.</summary>
internal sealed class UnreachableOutcomeSource(string reason) : IOutcomeSource
{
    public Task<IReadOnlyList<OutcomeSignal>> PollAsync(CancellationToken cancellationToken) =>
        throw new InvalidOperationException(reason);
}

/// <summary>
/// A learning store that records every outcome handed to it and reports it as
/// newly-recorded the first time and already-recorded on any repeat, exactly
/// like the real Cosmos-backed store's idempotency contract.
/// </summary>
internal sealed class RecordingLearningStore : ILearningStore
{
    private readonly HashSet<(string IntentKey, string Verdict)> seen = [];

    public List<LearningRecord> Recorded { get; } = [];

    public Task<bool> RecordAsync(LearningRecord record, CancellationToken cancellationToken)
    {
        Recorded.Add(record);
        return Task.FromResult(seen.Add((record.IntentKey, record.Verdict)));
    }

    public Task<IReadOnlyList<LearningRecord>> RetrieveAsync(string intentKey, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<LearningRecord>>(
            Recorded.Where(record => record.IntentKey == intentKey).ToArray());
}

/// <summary>A learning store whose backend cannot be reached.</summary>
internal sealed class UnreachableLearningStore(string reason) : ILearningStore
{
    public Task<bool> RecordAsync(LearningRecord record, CancellationToken cancellationToken) =>
        throw new InvalidOperationException(reason);

    public Task<IReadOnlyList<LearningRecord>> RetrieveAsync(string intentKey, CancellationToken cancellationToken) =>
        throw new InvalidOperationException(reason);
}

/// <summary>Composes conveyor services from collaborators the test supplied directly.</summary>
internal sealed class ScriptedConveyorComposer(
    IReadOnlyList<IEvidenceGatherer> gatherers,
    IIssueFiler? issueFiler,
    IRunStore runStore,
    IModelClient modelClient,
    ITracer tracer,
    IConfidenceThresholdReader? confidenceThresholdReader = null) : IConveyorComposer
{
    public ConveyorServices ComposeFor(RuntimeSettings settings) =>
        new(
            settings.Product, gatherers, issueFiler, runStore, modelClient, tracer,
            confidenceThresholdReader ?? new FixedConfidenceThresholdReader(S5Council.DefaultThreshold));
}

/// <summary>A deterministic model client that answers a fixed, recorded completion for every prompt.</summary>
internal sealed class RecordingModelClient(string response = "deterministic test completion") : IModelClient
{
    public List<string> Prompts { get; } = [];

    public Task<string> CompleteAsync(string prompt, CancellationToken cancellationToken)
    {
        Prompts.Add(prompt);
        return Task.FromResult(response);
    }
}

/// <summary>A model client that always fails, so a model-dependent station's failure path can be exercised.</summary>
internal sealed class ThrowingModelClient(string reason) : IModelClient
{
    public Task<string> CompleteAsync(string prompt, CancellationToken cancellationToken) =>
        throw new InvalidOperationException(reason);
}

/// <summary>A tracer that records every event it was asked to send.</summary>
internal sealed class RecordingTracer : ITracer
{
    public List<(string Name, IReadOnlyDictionary<string, string?> Properties)> Traced { get; } = [];

    public Task TraceAsync(string name, IReadOnlyDictionary<string, string?> properties, CancellationToken cancellationToken)
    {
        Traced.Add((name, properties));
        return Task.CompletedTask;
    }
}

/// <summary>A tracer whose backend cannot be reached, so a tracing failure can be exercised without failing the run.</summary>
internal sealed class UnreachableTracer(string reason) : ITracer
{
    public Task TraceAsync(string name, IReadOnlyDictionary<string, string?> properties, CancellationToken cancellationToken) =>
        throw new InvalidOperationException(reason);
}

/// <summary>
/// A run store that records every persisted checkpoint in order and keeps the
/// last-saved document per run id, so a test can assert a resumed run finds and
/// continues what a prior save already persisted -- the same seam a real,
/// cross-process store round-trips through.
/// </summary>
internal sealed class RecordingRunStore : IRunStore
{
    private readonly Dictionary<string, ConveyorRun> documents = [];

    public List<(string RunId, string Station, RunStatus Status)> Saved { get; } = [];

    public Task SaveAsync(ConveyorRun run, string station, CancellationToken cancellationToken)
    {
        Saved.Add((run.Id, station, run.Status));
        documents[run.Id] = run;
        return Task.CompletedTask;
    }

    public Task<ConveyorRun?> LoadAsync(string runId, CancellationToken cancellationToken) =>
        Task.FromResult(documents.TryGetValue(runId, out var run) ? run : null);

    /// <summary>Seeds a persisted document directly, without going through <see cref="SaveAsync"/>.</summary>
    public void Seed(ConveyorRun run) => documents[run.Id] = run;
}

/// <summary>A run store whose backing store cannot be reached.</summary>
internal sealed class UnreachableRunStore(string reason) : IRunStore
{
    public Task SaveAsync(ConveyorRun run, string station, CancellationToken cancellationToken) =>
        throw new InvalidOperationException(reason);

    public Task<ConveyorRun?> LoadAsync(string runId, CancellationToken cancellationToken) =>
        throw new InvalidOperationException(reason);
}

/// <summary>A confidence threshold reader that always answers a fixed value.</summary>
internal sealed class FixedConfidenceThresholdReader(double threshold) : IConfidenceThresholdReader
{
    public Task<double> ReadThresholdAsync(CancellationToken cancellationToken) => Task.FromResult(threshold);
}

/// <summary>A confidence threshold reader whose backing store cannot be reached.</summary>
internal sealed class UnreachableConfidenceThresholdReader(string reason) : IConfidenceThresholdReader
{
    public Task<double> ReadThresholdAsync(CancellationToken cancellationToken) =>
        throw new InvalidOperationException(reason);
}

/// <summary>A source integration that yields fixed evidence for any kind.</summary>
internal sealed class ScriptedSourceIntegration(params EvidenceItem[] evidence) : ISourceIntegration
{
    public Task<IReadOnlyList<EvidenceItem>> GatherAsync(
        string kind, string product, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<EvidenceItem>>(evidence);
}

/// <summary>
/// An Azure Monitor logs gateway that answers a fixed, scripted set of rows for
/// any workspace/query, so <see cref="AzureMonitorIntegration"/> can be tested at
/// the <c>GatherAsync</c> seam without a live Log Analytics workspace.
/// </summary>
internal sealed class ScriptedAzureMonitorLogsGateway(
    params IReadOnlyDictionary<string, string>[] rows) : IAzureMonitorLogsGateway
{
    public string? RequestedWorkspaceId { get; private set; }
    public string? RequestedQuery { get; private set; }

    public Task<IReadOnlyList<IReadOnlyDictionary<string, string>>> QueryAsync(
        string workspaceId, string query, CancellationToken cancellationToken)
    {
        RequestedWorkspaceId = workspaceId;
        RequestedQuery = query;
        return Task.FromResult<IReadOnlyList<IReadOnlyDictionary<string, string>>>(rows);
    }
}

/// <summary>An Azure Monitor logs gateway whose workspace cannot be reached.</summary>
internal sealed class UnreachableAzureMonitorLogsGateway(string reason) : IAzureMonitorLogsGateway
{
    public Task<IReadOnlyList<IReadOnlyDictionary<string, string>>> QueryAsync(
        string workspaceId, string query, CancellationToken cancellationToken) =>
        throw new InvalidOperationException(reason);
}

/// <summary>
/// A FoundryIQ knowledge base gateway that answers a fixed, scripted set of
/// results for any project/knowledge base/query, so <see cref="FoundryIqIntegration"/>
/// can be tested at the <c>GatherAsync</c> seam without a live Foundry project.
/// </summary>
internal sealed class ScriptedFoundryIqKnowledgeGateway(params FoundryIqResult[] results) : IFoundryIqKnowledgeGateway
{
    public string? RequestedProjectEndpoint { get; private set; }
    public string? RequestedKnowledgeBase { get; private set; }
    public string? RequestedQuery { get; private set; }

    public Task<IReadOnlyList<FoundryIqResult>> QueryAsync(
        string projectEndpoint, string knowledgeBase, string query, CancellationToken cancellationToken)
    {
        RequestedProjectEndpoint = projectEndpoint;
        RequestedKnowledgeBase = knowledgeBase;
        RequestedQuery = query;
        return Task.FromResult<IReadOnlyList<FoundryIqResult>>(results);
    }
}

/// <summary>A FoundryIQ knowledge base gateway whose project cannot be reached.</summary>
internal sealed class UnreachableFoundryIqKnowledgeGateway(string reason) : IFoundryIqKnowledgeGateway
{
    public Task<IReadOnlyList<FoundryIqResult>> QueryAsync(
        string projectEndpoint, string knowledgeBase, string query, CancellationToken cancellationToken) =>
        throw new InvalidOperationException(reason);
}

/// <summary>
/// A WebIQ search gateway that answers a fixed, scripted set of results for any
/// API key/query, so <see cref="WebIqIntegration"/> can be tested at the
/// <c>GatherAsync</c> seam without a live WebIQ call.
/// </summary>
internal sealed class ScriptedWebIqSearchGateway(params WebIqResult[] results) : IWebIqSearchGateway
{
    public string? RequestedApiKey { get; private set; }
    public string? RequestedQuery { get; private set; }

    public Task<IReadOnlyList<WebIqResult>> SearchAsync(
        string apiKey, string query, CancellationToken cancellationToken)
    {
        RequestedApiKey = apiKey;
        RequestedQuery = query;
        return Task.FromResult<IReadOnlyList<WebIqResult>>(results);
    }
}

/// <summary>A WebIQ search gateway that cannot be reached.</summary>
internal sealed class UnreachableWebIqSearchGateway(string reason) : IWebIqSearchGateway
{
    public Task<IReadOnlyList<WebIqResult>> SearchAsync(
        string apiKey, string query, CancellationToken cancellationToken) =>
        throw new InvalidOperationException(reason);
}

/// <summary>
/// A Key Vault secret reader that answers a fixed, scripted secret value for
/// any vault/secret name, so callers reading a secret (e.g. <see
/// cref="WebIqIntegration"/>'s API key) can be tested without a live Key Vault.
/// </summary>
internal sealed class ScriptedPrivateKeySecretReader(string secretValue) : Dsf.Runtime.GitHubApp.IPrivateKeySecretReader
{
    public Uri? RequestedVaultUri { get; private set; }
    public string? RequestedSecretName { get; private set; }

    public Task<string> GetSecretAsync(Uri vaultUri, string secretName, CancellationToken cancellationToken)
    {
        RequestedVaultUri = vaultUri;
        RequestedSecretName = secretName;
        return Task.FromResult(secretValue);
    }
}

/// <summary>A Key Vault secret reader whose vault cannot be reached.</summary>
internal sealed class UnreachablePrivateKeySecretReader(string reason) : Dsf.Runtime.GitHubApp.IPrivateKeySecretReader
{
    public Task<string> GetSecretAsync(Uri vaultUri, string secretName, CancellationToken cancellationToken) =>
        throw new InvalidOperationException(reason);
}

/// <summary>
/// A sweep control store that keeps its state in memory instead of a real App
/// Configuration store, so the CLI's pause/resume/interval/status subcommands and
/// the sweep loop's per-tick checks can be tested deterministically.
/// </summary>
internal sealed class ScriptedSweepControlStore(SweepControlState? initial = null) : ISweepControlStore
{
    public SweepControlState State { get; private set; } = initial ?? SweepControlState.Unset;

    public Task<SweepControlState> ReadAsync(CancellationToken cancellationToken) => Task.FromResult(State);

    public Task SetPausedAsync(bool paused, CancellationToken cancellationToken)
    {
        State = State with { Paused = paused };
        return Task.CompletedTask;
    }

    public Task SetIntervalSecondsAsync(int seconds, CancellationToken cancellationToken)
    {
        State = State with { IntervalSeconds = seconds };
        return Task.CompletedTask;
    }
}

/// <summary>A sweep control store whose backing store cannot be reached.</summary>
internal sealed class UnreachableSweepControlStore(string reason) : ISweepControlStore
{
    private RuntimeConfigurationException Failure() =>
        new($"failed to reach the sweep control store: {reason}", [RuntimeSettingsComposer.AzureAppConfigEndpoint]);

    public Task<SweepControlState> ReadAsync(CancellationToken cancellationToken) => throw Failure();

    public Task SetPausedAsync(bool paused, CancellationToken cancellationToken) => throw Failure();

    public Task SetIntervalSecondsAsync(int seconds, CancellationToken cancellationToken) => throw Failure();
}

/// <summary>A sweep lease that always answers a fixed, scripted acquisition result.</summary>
internal sealed class ScriptedSweepLease(bool acquires = true) : ISweepLease
{
    public List<(string Product, DateTimeOffset Now, TimeSpan Interval)> Requests { get; } = [];

    public Task<bool> TryAcquireAsync(
        string product, DateTimeOffset now, TimeSpan interval, CancellationToken cancellationToken)
    {
        Requests.Add((product, now, interval));
        return Task.FromResult(acquires);
    }
}

/// <summary>An owner runtime index that is never expected to be consulted.</summary>
internal sealed class RecordingOwnerRuntimeIndexReader(IReadOnlyDictionary<string, string>? values = null)
    : IOwnerRuntimeIndexReader
{
    public string? RequestedProduct { get; private set; }

    public Task<IReadOnlyDictionary<string, string>> ReadAsync(
        string ownerAppConfigEndpoint, string product, CancellationToken cancellationToken)
    {
        RequestedProduct = product;
        return Task.FromResult(values ?? new Dictionary<string, string>());
    }
}

/// <summary>A source agent roster resolved from a fixed, test-supplied list.</summary>
internal sealed class RosterReader(IReadOnlyList<string> kinds) : ISourceAgentRosterReader
{
    public RuntimeSettings? RequestedSettings { get; private set; }

    public Task<IReadOnlyList<string>> ReadEnabledKindsAsync(
        RuntimeSettings settings, CancellationToken cancellationToken)
    {
        RequestedSettings = settings;
        return Task.FromResult(kinds);
    }
}

/// <summary>A host runner that records the app it was handed instead of blocking on it.</summary>
internal sealed class RecordingWebHostRunner : IWebHostRunner
{
    public Microsoft.AspNetCore.Builder.WebApplication? Started { get; private set; }

    public Task RunAsync(Microsoft.AspNetCore.Builder.WebApplication app, CancellationToken cancellationToken)
    {
        Started = app;
        return Task.CompletedTask;
    }
}

/// <summary>An evidence gatherer that yields fixed evidence for one source kind.</summary>
internal sealed class ScriptedEvidenceGatherer(string sourceKind, params EvidenceItem[] evidence)
    : IEvidenceGatherer
{
    public string SourceKind { get; } = sourceKind;

    public Task<IReadOnlyList<EvidenceItem>> GatherAsync(ConveyorRun run, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<EvidenceItem>>(evidence);
}

/// <summary>An issue filer that records what it was asked to file.</summary>
internal sealed class RecordingIssueFiler : IIssueFiler
{
    public List<Proposal> Filed { get; } = [];

    public Task<string> FileAsync(Proposal proposal, CancellationToken cancellationToken)
    {
        Filed.Add(proposal);
        return Task.FromResult($"https://github.com/acme/acme/issues/{Filed.Count}");
    }
}

/// <summary>A roster store that cannot be read (unauthorized, unreachable, ...).</summary>
internal sealed class UnreachableRosterReader(string reason) : ISourceAgentRosterReader
{
    public Task<IReadOnlyList<string>> ReadEnabledKindsAsync(
        RuntimeSettings settings, CancellationToken cancellationToken) =>
        throw new RuntimeConfigurationException(
            $"failed to read the source agent roster for product '{settings.Product}': {reason}",
            [RuntimeSettingsComposer.AzureAppConfigEndpoint]);
}
