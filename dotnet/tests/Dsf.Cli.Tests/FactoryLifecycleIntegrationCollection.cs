using Xunit;

namespace Dsf.Cli.Tests;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class FactoryLifecycleIntegrationCollection
{
    public const string Name = "Factory lifecycle integration";
}
