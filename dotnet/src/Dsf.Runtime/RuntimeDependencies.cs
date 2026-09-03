using Dsf.Core.Runtime;
using Dsf.FeatureCouncil.Conveyor;
using Microsoft.AspNetCore.Builder;

namespace Dsf.Runtime;

/// <summary>
/// Runs a built web host until the process is asked to stop. Extracted so a test
/// can assert the runtime built and handed over a real, startable host without
/// blocking the test run on a server that never returns.
/// </summary>
public interface IWebHostRunner
{
    Task RunAsync(WebApplication app, CancellationToken cancellationToken);
}

/// <summary>Runs the host for real: serves until cancelled.</summary>
internal sealed class WebApplicationHostRunner : IWebHostRunner
{
    public Task RunAsync(WebApplication app, CancellationToken cancellationToken) => app.RunAsync();
}

/// <summary>
/// The collaborators the runtime verbs need, resolved once per process.
/// <see cref="Production"/> wires the real adapters only (ADR 0014): the
/// managed-identity Azure readers and the real web host runner. The source agent
/// gatherers and the GitHub issue filer are empty/unset until #144 and #143 land
/// them -- the conveyor stations report that absence out loud rather than
/// substituting a do-nothing implementation.
/// </summary>
public sealed record RuntimeDependencies(
    IOwnerRuntimeIndexReader OwnerRuntimeIndexReader,
    ISourceAgentRosterReader SourceAgentRosterReader,
    IWebHostRunner WebHostRunner,
    IReadOnlyList<IEvidenceGatherer> EvidenceGatherers,
    IIssueFiler? IssueFiler)
{
    public static RuntimeDependencies Production() => new(
        new AzureAppConfigurationOwnerRuntimeIndexReader(),
        new AzureAppConfigurationSourceAgentRosterReader(),
        new WebApplicationHostRunner(),
        [],
        null);

    /// <summary>The conveyor collaborators for <paramref name="settings"/>'s product.</summary>
    public ConveyorServices ConveyorServicesFor(RuntimeSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return new ConveyorServices(settings.Product, EvidenceGatherers, IssueFiler);
    }
}
