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
/// <see cref="Production(IReadOnlyDictionary{string, string})"/> wires the real
/// adapters only (ADR 0014): the managed-identity Azure readers, the real web host
/// runner, the environment-driven conveyor composition (A2A gatherers, GitHub
/// filer, Cosmos-backed run store) and the served agent's upstream integration.
/// None of them is optional or empty -- an unconfigured dependency is reported by
/// the setting that is unset, at composition time.
/// </summary>
public sealed record RuntimeDependencies(
    IOwnerRuntimeIndexReader OwnerRuntimeIndexReader,
    ISourceAgentRosterReader SourceAgentRosterReader,
    IWebHostRunner WebHostRunner,
    IConveyorComposer ConveyorComposer,
    ISourceIntegration SourceIntegration,
    ILearningComposer LearningComposer)
{
    /// <summary>Production dependencies resolved from the real process environment.</summary>
    public static RuntimeDependencies Production() => Production(CurrentEnvironment());

    /// <summary>Production dependencies resolved from <paramref name="env"/>.</summary>
    public static RuntimeDependencies Production(IReadOnlyDictionary<string, string?> env)
    {
        ArgumentNullException.ThrowIfNull(env);
        return new(
            new AzureAppConfigurationOwnerRuntimeIndexReader(),
            new AzureAppConfigurationSourceAgentRosterReader(),
            new WebApplicationHostRunner(),
            new EnvironmentConveyorComposer(env),
            new HttpSourceIntegration(env),
            new EnvironmentLearningComposer(env));
    }

    /// <summary>The learning loop's collaborators for <paramref name="settings"/>'s product.</summary>
    public LearningServices LearningServicesFor(RuntimeSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return LearningComposer.ComposeFor(settings);
    }

    /// <summary>
    /// The conveyor collaborators for <paramref name="settings"/>'s product.
    /// Throws <see cref="RuntimeConfigurationException"/> naming every unset
    /// setting when the composition is incomplete.
    /// </summary>
    public ConveyorServices ConveyorServicesFor(RuntimeSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return ConveyorComposer.ComposeFor(settings);
    }

    private static IReadOnlyDictionary<string, string?> CurrentEnvironment()
    {
        var result = new Dictionary<string, string?>();
        foreach (System.Collections.DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            result[(string)entry.Key] = entry.Value as string;
        }

        return result;
    }
}
