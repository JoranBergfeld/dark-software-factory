using Dsf.Cli;
using Dsf.Core.Charters;
using Xunit;

namespace Dsf.Cli.Tests;

public sealed class CharterCommandTests
{
    private const string ValidCharter = """
        <!-- dsf:charter schema_version=1 -->
        # Product Charter: demo

        ## Vision
        Ship demo value.

        ## Target Users
        Operators.

        ## Goals
        - Deliver value

        ## Non-Goals
        - Boil the ocean

        ## Success Metrics
        - Weekly active operators

        ## Constraints
        Azure only.

        ## Glossary
        - Charter: human-owned intent
        """;

    [Fact]
    public async Task CharterSync_resolves_the_product_from_owner_app_config_and_reads_its_repository()
    {
        await WithOwnerEndpointAsync(async () =>
        {
            var terminal = NonInteractiveTerminal();
            var appConfig = new RecordingAppConfigurationClient(
                new ProductLocation("demo", "acme/demo", "https://demo.azconfig.io"));
            var repository = new RecordingCharterRepositoryClient(new CharterFile(ValidCharter, "abc123"));
            var store = new RecordingCharterStore();

            var exitCode = await CliApplication.InvokeAsync(
                ["charter", "sync", "--product", "demo", "--ref", "main"],
                CancellationToken.None,
                terminal,
                appConfig,
                repository,
                store);

            Assert.Equal(0, exitCode);
            Assert.Equal(("acme/demo", ".dsf/charter.md", "main"), Assert.Single(repository.Reads));
            Assert.Contains("[dsf] synced charter for demo: OK", terminal.Output, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task CharterSync_persists_the_parsed_charter_with_repository_sha_and_timestamp()
    {
        await WithOwnerEndpointAsync(async () =>
        {
            var store = new RecordingCharterStore();

            var exitCode = await CliApplication.InvokeAsync(
                ["charter", "sync", "--product", "demo", "--ref", "release"],
                CancellationToken.None,
                NonInteractiveTerminal(),
                new RecordingAppConfigurationClient(
                    new ProductLocation("demo", "acme/demo", "https://demo.azconfig.io")),
                new RecordingCharterRepositoryClient(new CharterFile(ValidCharter, "abc123")),
                store);

            Assert.Equal(0, exitCode);
            var stored = Assert.Single(store.Writes);
            Assert.Equal("demo", stored.Product);
            Assert.Equal("acme/demo", stored.Repository);
            Assert.Equal(CharterStatus.Ok, stored.Status);
            Assert.Equal("abc123", stored.SourceSha);
            Assert.Equal("release", stored.SourceRef);
            Assert.Equal(ValidCharter, stored.Content);
            Assert.NotNull(stored.LastSyncedAt);
            Assert.NotNull(stored.Charter);
            Assert.Equal("Ship demo value.", stored.Charter!.Vision);
            Assert.Equal("abc123", stored.Charter!.SourceSha);
        });
    }

    [Fact]
    public async Task CharterSync_is_idempotent_on_an_unchanged_blob_sha()
    {
        await WithOwnerEndpointAsync(async () =>
        {
            var store = new RecordingCharterStore();
            var appConfig = new RecordingAppConfigurationClient(
                new ProductLocation("demo", "acme/demo", "https://demo.azconfig.io"));
            var repository = new RecordingCharterRepositoryClient(new CharterFile(ValidCharter, "abc123"));

            await CliApplication.InvokeAsync(
                ["charter", "sync", "--product", "demo", "--ref", "main"],
                CancellationToken.None,
                NonInteractiveTerminal(),
                appConfig,
                repository,
                store);
            var exitCode = await CliApplication.InvokeAsync(
                ["charter", "sync", "--product", "demo", "--ref", "main"],
                CancellationToken.None,
                NonInteractiveTerminal(),
                appConfig,
                repository,
                store);

            Assert.Equal(0, exitCode);
            Assert.Single(store.Writes);
        });
    }

    [Fact]
    public async Task CharterSync_records_INVALID_and_keeps_the_last_known_good_charter()
    {
        await WithOwnerEndpointAsync(async () =>
        {
            var store = new RecordingCharterStore();
            var appConfig = new RecordingAppConfigurationClient(
                new ProductLocation("demo", "acme/demo", "https://demo.azconfig.io"));

            await CliApplication.InvokeAsync(
                ["charter", "sync", "--product", "demo", "--ref", "main"],
                CancellationToken.None,
                NonInteractiveTerminal(),
                appConfig,
                new RecordingCharterRepositoryClient(new CharterFile(ValidCharter, "abc123")),
                store);

            var terminal = NonInteractiveTerminal();
            var exitCode = await CliApplication.InvokeAsync(
                ["charter", "sync", "--product", "demo", "--ref", "main"],
                CancellationToken.None,
                terminal,
                appConfig,
                new RecordingCharterRepositoryClient(new CharterFile("# broken\n", "def456")),
                store);

            Assert.Equal(1, exitCode);
            Assert.Contains("[dsf] synced charter for demo: INVALID", terminal.Output, StringComparison.Ordinal);
            var stored = store.Writes[^1];
            Assert.Equal(CharterStatus.Invalid, stored.Status);
            Assert.NotNull(stored.LastError);
            Assert.NotNull(stored.Charter);
            Assert.Equal("Ship demo value.", stored.Charter!.Vision);
        });
    }

    [Fact]
    public async Task CharterSync_records_MISSING_when_the_repository_has_no_charter_file()
    {
        await WithOwnerEndpointAsync(async () =>
        {
            var store = new RecordingCharterStore();
            var terminal = NonInteractiveTerminal();

            var exitCode = await CliApplication.InvokeAsync(
                ["charter", "sync", "--product", "demo", "--ref", "main"],
                CancellationToken.None,
                terminal,
                new RecordingAppConfigurationClient(
                    new ProductLocation("demo", "acme/demo", "https://demo.azconfig.io")),
                new RecordingCharterRepositoryClient(null),
                store);

            Assert.Equal(0, exitCode);
            Assert.Contains("[dsf] synced charter for demo: MISSING", terminal.Output, StringComparison.Ordinal);
            var stored = Assert.Single(store.Writes);
            Assert.Equal(CharterStatus.Missing, stored.Status);
            Assert.Contains(".dsf/charter.md", stored.LastError!, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task CharterStatus_reports_ok_when_the_stored_sha_matches_the_repository_file()
    {
        await WithOwnerEndpointAsync(async () =>
        {
            var store = new RecordingCharterStore();
            var appConfig = new RecordingAppConfigurationClient(
                new ProductLocation("demo", "acme/demo", "https://demo.azconfig.io"));
            var repository = new RecordingCharterRepositoryClient(new CharterFile(ValidCharter, "abc123"));
            await CliApplication.InvokeAsync(
                ["charter", "sync", "--product", "demo", "--ref", "main"],
                CancellationToken.None,
                NonInteractiveTerminal(),
                appConfig,
                repository,
                store);

            var terminal = NonInteractiveTerminal();
            var exitCode = await CliApplication.InvokeAsync(
                ["charter", "status", "--product", "demo", "--ref", "main"],
                CancellationToken.None,
                terminal,
                appConfig,
                repository,
                store);

            Assert.Equal(0, exitCode);
            Assert.Contains("[dsf] charter demo: ok", terminal.Output, StringComparison.Ordinal);
            Assert.Contains("stored_sha=abc123 ref=main", terminal.Output, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task CharterStatus_reports_stale_when_the_repository_file_drifted_from_the_stored_charter()
    {
        await WithOwnerEndpointAsync(async () =>
        {
            var store = new RecordingCharterStore();
            var appConfig = new RecordingAppConfigurationClient(
                new ProductLocation("demo", "acme/demo", "https://demo.azconfig.io"));
            await CliApplication.InvokeAsync(
                ["charter", "sync", "--product", "demo", "--ref", "main"],
                CancellationToken.None,
                NonInteractiveTerminal(),
                appConfig,
                new RecordingCharterRepositoryClient(new CharterFile(ValidCharter, "abc123")),
                store);

            var terminal = NonInteractiveTerminal();
            var exitCode = await CliApplication.InvokeAsync(
                ["charter", "status", "--product", "demo", "--ref", "main"],
                CancellationToken.None,
                terminal,
                appConfig,
                new RecordingCharterRepositoryClient(new CharterFile(ValidCharter, "def456")),
                store);

            Assert.Equal(0, exitCode);
            Assert.Contains("[dsf] charter demo: stale", terminal.Output, StringComparison.Ordinal);
            Assert.Contains("file_sha=def456", terminal.Output, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task CharterStatus_reports_stale_when_nothing_is_stored_yet()
    {
        await WithOwnerEndpointAsync(async () =>
        {
            var terminal = NonInteractiveTerminal();

            var exitCode = await CliApplication.InvokeAsync(
                ["charter", "status", "--product", "demo", "--ref", "main"],
                CancellationToken.None,
                terminal,
                new RecordingAppConfigurationClient(
                    new ProductLocation("demo", "acme/demo", "https://demo.azconfig.io")),
                new RecordingCharterRepositoryClient(new CharterFile(ValidCharter, "abc123")),
                new RecordingCharterStore());

            Assert.Equal(0, exitCode);
            Assert.Contains("[dsf] charter demo: stale", terminal.Output, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task CharterStatus_reports_missing_when_the_repository_file_is_absent()
    {
        await WithOwnerEndpointAsync(async () =>
        {
            var terminal = NonInteractiveTerminal();

            var exitCode = await CliApplication.InvokeAsync(
                ["charter", "status", "--product", "demo", "--ref", "main"],
                CancellationToken.None,
                terminal,
                new RecordingAppConfigurationClient(
                    new ProductLocation("demo", "acme/demo", "https://demo.azconfig.io")),
                new RecordingCharterRepositoryClient(null),
                new RecordingCharterStore());

            Assert.Equal(0, exitCode);
            Assert.Contains("[dsf] charter demo: missing", terminal.Output, StringComparison.Ordinal);
            Assert.Contains("note:", terminal.Output, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task CharterSync_fails_loudly_when_the_charter_store_is_not_configured()
    {
        await WithOwnerEndpointAsync(async () =>
        {
            var terminal = NonInteractiveTerminal();

            var exitCode = await CliApplication.InvokeAsync(
                ["charter", "sync", "--product", "demo", "--ref", "main"],
                CancellationToken.None,
                terminal,
                new RecordingAppConfigurationClient(
                    new ProductLocation("demo", "acme/demo", "https://demo.azconfig.io")),
                new RecordingCharterRepositoryClient(new CharterFile(ValidCharter, "abc123")),
                new UnconfiguredCharterStore());

            Assert.Equal(1, exitCode);
            Assert.Contains("AZURE_COSMOS_ENDPOINT", terminal.Error, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task CharterStatus_fails_loudly_when_the_owner_app_config_endpoint_is_missing()
    {
        var prior = Environment.GetEnvironmentVariable("DSF_OWNER_APPCONFIG_ENDPOINT");
        Environment.SetEnvironmentVariable("DSF_OWNER_APPCONFIG_ENDPOINT", null);
        try
        {
            var terminal = NonInteractiveTerminal();

            var exitCode = await CliApplication.InvokeAsync(
                ["charter", "status", "--product", "demo"],
                CancellationToken.None,
                terminal,
                new RecordingAppConfigurationClient(),
                new RecordingCharterRepositoryClient(null),
                new RecordingCharterStore());

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
                repository,
                new RecordingCharterStore());

            Assert.Equal(0, exitCode);
            var pullRequest = Assert.Single(repository.InitialPullRequests);
            Assert.Equal(("acme/demo", "demo"), (pullRequest.Repository, pullRequest.Product));
            Assert.Contains("# Product Charter: demo", pullRequest.Content, StringComparison.Ordinal);
            Assert.Contains("[dsf] opened charter PR:", terminal.Output, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task CharterImplement_fails_loudly_when_the_owner_app_config_endpoint_is_missing()
    {
        var prior = Environment.GetEnvironmentVariable("DSF_OWNER_APPCONFIG_ENDPOINT");
        Environment.SetEnvironmentVariable("DSF_OWNER_APPCONFIG_ENDPOINT", null);
        try
        {
            var terminal = NonInteractiveTerminal();

            var exitCode = await CliApplication.InvokeAsync(
                ["charter", "implement", "--product", "demo"],
                CancellationToken.None,
                terminal,
                new RecordingAppConfigurationClient(),
                new RecordingCharterRepositoryClient(null),
                new RecordingCharterStore());

            Assert.Equal(1, exitCode);
            Assert.Contains("DSF_OWNER_APPCONFIG_ENDPOINT", terminal.Error, StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable("DSF_OWNER_APPCONFIG_ENDPOINT", prior);
        }
    }

    [Fact]
    public async Task CharterImplement_fails_loudly_when_the_product_is_not_provisioned()
    {
        await WithOwnerEndpointAsync(async () =>
        {
            var terminal = NonInteractiveTerminal();

            var exitCode = await CliApplication.InvokeAsync(
                ["charter", "implement", "--product", "unknown"],
                CancellationToken.None,
                terminal,
                new RecordingAppConfigurationClient(),
                new RecordingCharterRepositoryClient(null),
                new RecordingCharterStore());

            Assert.Equal(1, exitCode);
            Assert.Contains("[dsf] error:", terminal.Error, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task CharterImplement_resolves_the_product_repository_before_reporting_the_migration_shell()
    {
        await WithOwnerEndpointAsync(async () =>
        {
            var terminal = NonInteractiveTerminal();
            var appConfig = new RecordingAppConfigurationClient(
                new ProductLocation("demo", "acme/demo", "https://demo.azconfig.io"));

            var exitCode = await CliApplication.InvokeAsync(
                ["charter", "implement", "--product", "demo"],
                CancellationToken.None,
                terminal,
                appConfig,
                new RecordingCharterRepositoryClient(null),
                new RecordingCharterStore());

            Assert.Equal(0, exitCode);
            Assert.Contains(
                "[dsf] charter implement is not implemented in the .NET migration shell.",
                terminal.Output,
                StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task CharterWatch_fails_loudly_when_the_owner_app_config_endpoint_is_missing()
    {
        var prior = Environment.GetEnvironmentVariable("DSF_OWNER_APPCONFIG_ENDPOINT");
        Environment.SetEnvironmentVariable("DSF_OWNER_APPCONFIG_ENDPOINT", null);
        try
        {
            var terminal = NonInteractiveTerminal();

            var exitCode = await CliApplication.InvokeAsync(
                ["charter", "watch", "--product", "demo"],
                CancellationToken.None,
                terminal,
                new RecordingAppConfigurationClient(),
                new RecordingCharterRepositoryClient(null),
                new RecordingCharterStore());

            Assert.Equal(1, exitCode);
            Assert.Contains("DSF_OWNER_APPCONFIG_ENDPOINT", terminal.Error, StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable("DSF_OWNER_APPCONFIG_ENDPOINT", prior);
        }
    }

    [Fact]
    public async Task CharterWatch_fails_loudly_when_the_product_is_not_provisioned()
    {
        await WithOwnerEndpointAsync(async () =>
        {
            var terminal = NonInteractiveTerminal();

            var exitCode = await CliApplication.InvokeAsync(
                ["charter", "watch", "--product", "unknown"],
                CancellationToken.None,
                terminal,
                new RecordingAppConfigurationClient(),
                new RecordingCharterRepositoryClient(null),
                new RecordingCharterStore());

            Assert.Equal(1, exitCode);
            Assert.Contains("[dsf] error:", terminal.Error, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task CharterWatch_resolves_the_product_repository_before_reporting_the_migration_shell()
    {
        await WithOwnerEndpointAsync(async () =>
        {
            var terminal = NonInteractiveTerminal();
            var appConfig = new RecordingAppConfigurationClient(
                new ProductLocation("demo", "acme/demo", "https://demo.azconfig.io"));

            var exitCode = await CliApplication.InvokeAsync(
                ["charter", "watch", "--product", "demo"],
                CancellationToken.None,
                terminal,
                appConfig,
                new RecordingCharterRepositoryClient(null),
                new RecordingCharterStore());

            Assert.Equal(0, exitCode);
            Assert.Contains(
                "[dsf] charter watch is not implemented in the .NET migration shell.",
                terminal.Output,
                StringComparison.Ordinal);
        });
    }

    private static ScriptedTerminal NonInteractiveTerminal() => new(
        new TerminalCapabilities(IsInteractive: false, SupportsAnsi: false, SupportsEmoji: false),
        []);

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

/// <summary>Deterministic in-memory charter store; test-only double for <see cref="ICharterStore"/>.</summary>
internal sealed class RecordingCharterStore : ICharterStore
{
    private readonly Dictionary<string, StoredCharter> documents = new(StringComparer.Ordinal);

    public List<StoredCharter> Writes { get; } = [];

    public Task<StoredCharter?> GetCharterAsync(string product, CancellationToken cancellationToken) =>
        Task.FromResult(documents.GetValueOrDefault(product));

    public Task PutCharterAsync(StoredCharter stored, CancellationToken cancellationToken)
    {
        documents[stored.Product] = stored;
        Writes.Add(stored);
        return Task.CompletedTask;
    }
}

/// <summary>Test-only double standing in for a store whose Cosmos endpoint is unset.</summary>
internal sealed class UnconfiguredCharterStore : ICharterStore
{
    public Task<StoredCharter?> GetCharterAsync(string product, CancellationToken cancellationToken) =>
        throw new InvalidOperationException(
            "AZURE_COSMOS_ENDPOINT is required to read or write the stored charter.");

    public Task PutCharterAsync(StoredCharter stored, CancellationToken cancellationToken) =>
        throw new InvalidOperationException(
            "AZURE_COSMOS_ENDPOINT is required to read or write the stored charter.");
}
