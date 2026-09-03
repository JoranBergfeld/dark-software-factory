using Dsf.Cli;
using Xunit;

namespace Dsf.Cli.Tests;

public sealed class CharterCommandTests
{
    [Fact]
    public async Task CharterSync_resolves_the_product_from_owner_app_config_and_reads_its_repository()
    {
        await WithOwnerEndpointAsync(async () =>
        {
            var terminal = new ScriptedTerminal(
                new TerminalCapabilities(IsInteractive: false, SupportsAnsi: false, SupportsEmoji: false),
                []);
            var appConfig = new RecordingAppConfigurationClient(
                new ProductLocation("demo", "acme/demo", "https://demo.azconfig.io"));
            var repository = new RecordingCharterRepositoryClient(
                new CharterFile(
                    """
                    <!-- dsf:charter schema_version=1 -->
                    # Product Charter: demo
                    """,
                    "abc123"));

            var exitCode = await CliApplication.InvokeAsync(
                ["charter", "sync", "--product", "demo", "--ref", "main"],
                CancellationToken.None,
                terminal,
                appConfig,
                repository);

            Assert.Equal(0, exitCode);
            Assert.Equal(("acme/demo", ".dsf/charter.md", "main"), Assert.Single(repository.Reads));
            Assert.Contains("[dsf] synced charter for demo: OK", terminal.Output, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task CharterStatus_fails_loudly_when_the_owner_app_config_endpoint_is_missing()
    {
        var prior = Environment.GetEnvironmentVariable("DSF_OWNER_APPCONFIG_ENDPOINT");
        Environment.SetEnvironmentVariable("DSF_OWNER_APPCONFIG_ENDPOINT", null);
        try
        {
            var terminal = new ScriptedTerminal(
                new TerminalCapabilities(IsInteractive: false, SupportsAnsi: false, SupportsEmoji: false),
                []);

            var exitCode = await CliApplication.InvokeAsync(
                ["charter", "status", "--product", "demo"],
                CancellationToken.None,
                terminal,
                new RecordingAppConfigurationClient(),
                new RecordingCharterRepositoryClient(null));

            Assert.Equal(1, exitCode);
            Assert.Contains("DSF_OWNER_APPCONFIG_ENDPOINT", terminal.Error, StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable("DSF_OWNER_APPCONFIG_ENDPOINT", prior);
        }
    }

    [Fact]
    public async Task CharterInit_creates_a_pull_request_in_the_owner_index_repository()
    {
        await WithOwnerEndpointAsync(async () =>
        {
            var terminal = new ScriptedTerminal(
                new TerminalCapabilities(IsInteractive: true, SupportsAnsi: false, SupportsEmoji: false),
                ["vision", "users", "goal", "non-goal", "metric", "constraint"]);
            var repository = new RecordingCharterRepositoryClient(null);

            var exitCode = await CliApplication.InvokeAsync(
                ["charter", "init", "--product", "demo"],
                CancellationToken.None,
                terminal,
                new RecordingAppConfigurationClient(
                    new ProductLocation("demo", "acme/demo", "https://demo.azconfig.io")),
                repository);

            Assert.Equal(0, exitCode);
            var pullRequest = Assert.Single(repository.InitialPullRequests);
            Assert.Equal(("acme/demo", "demo"), (pullRequest.Repository, pullRequest.Product));
            Assert.Contains("# Product Charter: demo", pullRequest.Content, StringComparison.Ordinal);
            Assert.Contains("[dsf] opened charter PR:", terminal.Output, StringComparison.Ordinal);
        });
    }

    private static async Task WithOwnerEndpointAsync(Func<Task> action)
    {
        var prior = Environment.GetEnvironmentVariable("DSF_OWNER_APPCONFIG_ENDPOINT");
        Environment.SetEnvironmentVariable("DSF_OWNER_APPCONFIG_ENDPOINT", "https://owner.azconfig.io");
        try
        {
            await action();
        }
        finally
        {
            Environment.SetEnvironmentVariable("DSF_OWNER_APPCONFIG_ENDPOINT", prior);
        }
    }
}

internal sealed class RecordingAppConfigurationClient(params ProductLocation[] products)
    : IAppConfigurationClient
{
    public Task<IReadOnlyList<ProductLocation>> ListProductsAsync(
        string ownerEndpoint,
        CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<ProductLocation>>(products);

    public Task<ProductLocation> ResolveProductAsync(
        string ownerEndpoint,
        string product,
        CancellationToken cancellationToken) =>
        Task.FromResult(
            products.Single(candidate => candidate.Key == product));

    public Task SeedProductRecordAsync(
        string productEndpoint,
        Dsf.Core.Products.ProductRecord record,
        CancellationToken cancellationToken) => Task.CompletedTask;

    public Task<Dsf.Core.Products.ProductRecord> ReadProductRecordAsync(
        string productEndpoint,
        string product,
        CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public Task PublishRuntimeIndexAsync(
        string ownerEndpoint,
        string product,
        IReadOnlyDictionary<string, string> values,
        CancellationToken cancellationToken) => Task.CompletedTask;
}

internal sealed class RecordingCharterRepositoryClient(CharterFile? file) : ICharterRepositoryClient
{
    public List<(string Repository, string Path, string? Ref)> Reads { get; } = [];
    public List<(string Repository, string Product, string Content)> InitialPullRequests { get; } = [];

    public Task<CharterFile?> ReadAsync(
        string repository,
        string path,
        string? reference,
        CancellationToken cancellationToken)
    {
        Reads.Add((repository, path, reference));
        return Task.FromResult(file);
    }

    public Task<string> OpenInitialPullRequestAsync(
        string repository,
        string product,
        string content,
        CancellationToken cancellationToken)
    {
        InitialPullRequests.Add((repository, product, content));
        return Task.FromResult("https://github.test/acme/demo/pull/1");
    }
}
