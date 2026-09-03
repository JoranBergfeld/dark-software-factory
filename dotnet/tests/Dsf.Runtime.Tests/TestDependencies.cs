using Dsf.Core.Runtime;
using Dsf.FeatureCouncil.Conveyor;

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
        ISourceIntegration? sourceIntegration = null) =>
        new(
            ownerRuntimeIndexReader ?? new RecordingOwnerRuntimeIndexReader(),
            sourceAgentRosterReader ?? new RosterReader([]),
            webHostRunner ?? new RecordingWebHostRunner(),
            new ScriptedConveyorComposer(evidenceGatherers ?? [], issueFiler, runStore ?? new RecordingRunStore()),
            sourceIntegration ?? new ScriptedSourceIntegration());
}

/// <summary>Composes conveyor services from collaborators the test supplied directly.</summary>
internal sealed class ScriptedConveyorComposer(
    IReadOnlyList<IEvidenceGatherer> gatherers,
    IIssueFiler? issueFiler,
    IRunStore runStore) : IConveyorComposer
{
    public ConveyorServices ComposeFor(RuntimeSettings settings) =>
        new(settings.Product, gatherers, issueFiler, runStore);
}

/// <summary>A run store that records every persisted checkpoint in order.</summary>
internal sealed class RecordingRunStore : IRunStore
{
    public List<(string RunId, string Station, RunStatus Status)> Saved { get; } = [];

    public Task SaveAsync(ConveyorRun run, string station, CancellationToken cancellationToken)
    {
        Saved.Add((run.Id, station, run.Status));
        return Task.CompletedTask;
    }
}

/// <summary>A run store whose backing store cannot be reached.</summary>
internal sealed class UnreachableRunStore(string reason) : IRunStore
{
    public Task SaveAsync(ConveyorRun run, string station, CancellationToken cancellationToken) =>
        throw new InvalidOperationException(reason);
}

/// <summary>A source integration that yields fixed evidence for any kind.</summary>
internal sealed class ScriptedSourceIntegration(params EvidenceItem[] evidence) : ISourceIntegration
{
    public Task<IReadOnlyList<EvidenceItem>> GatherAsync(
        string kind, string product, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<EvidenceItem>>(evidence);
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
