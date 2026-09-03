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
            Assert.Contains(("acme/demo", ".dsf/charter.md", "main"), repository.Reads);
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
    public async Task CharterImplement_opens_constitution_pr_and_files_bootstrap_issue_after_syncing_repo_charter()
    {
        await WithOwnerEndpointAsync(async () =>
        {
            var terminal = NonInteractiveTerminal();
            var appConfig = new RecordingAppConfigurationClient(
                new ProductLocation("demo", "acme/demo", "https://demo.azconfig.io"));
            var repository = new RecordingCharterRepositoryClient(new CharterFile(ValidCharter, "abc123"))
            {
                MarkConstitutionCurrentAfterOpeningPullRequest = true,
            };

            var exitCode = await CliApplication.InvokeAsync(
                ["charter", "implement", "--product", "demo", "--no-wait"],
                CancellationToken.None,
                terminal,
                appConfig,
                repository,
                new RecordingCharterStore());

            Assert.Equal(0, exitCode);
            Assert.Contains(("acme/demo", ".dsf/charter.md", "main"), repository.Reads);
            var pullRequest = Assert.Single(repository.FilePullRequests);
            Assert.Equal("acme/demo", pullRequest.Repository);
            Assert.Equal(".specify/memory/constitution.md", pullRequest.Path);
            Assert.StartsWith("charter/constitution-abc123-", pullRequest.Branch, StringComparison.Ordinal);
            Assert.Equal("Add Spec Kit constitution for demo", pullRequest.Title);
            Assert.Equal("docs: add spec kit constitution for demo", pullRequest.Message);
            Assert.True(pullRequest.EnableAutoMerge);
            Assert.Contains("source_sha=abc123", pullRequest.Content, StringComparison.Ordinal);
            var issue = Assert.Single(repository.Issues);
            Assert.Equal("Build demo from its charter (Spec Kit)", issue.Title);
            Assert.Equal(["creation:ready"], issue.Labels);
            Assert.Contains("Bootstrap the **demo** product", issue.Body, StringComparison.Ordinal);
            Assert.Contains(".specify/memory/constitution.md", issue.Body, StringComparison.Ordinal);
            Assert.Contains(".dsf/charter.md", issue.Body, StringComparison.Ordinal);
            Assert.Contains("filed bootstrap issue", terminal.Output, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task CharterImplement_refuses_invalid_charter()
    {
        await WithOwnerEndpointAsync(async () =>
        {
            var terminal = NonInteractiveTerminal();
            var repository = new RecordingCharterRepositoryClient(new CharterFile("# broken\n", "bad"));

            var exitCode = await CliApplication.InvokeAsync(
                ["charter", "implement", "--product", "demo", "--no-wait"],
                CancellationToken.None,
                terminal,
                new RecordingAppConfigurationClient(
                    new ProductLocation("demo", "acme/demo", "https://demo.azconfig.io")),
                repository,
                new RecordingCharterStore());

            Assert.Equal(1, exitCode);
            Assert.Empty(repository.FilePullRequests);
            Assert.Empty(repository.Issues);
            Assert.Contains("charter for demo on main is invalid", terminal.Error, StringComparison.OrdinalIgnoreCase);
        });
    }

    [Fact]
    public async Task CharterImplement_refuses_missing_charter()
    {
        await WithOwnerEndpointAsync(async () =>
        {
            var terminal = NonInteractiveTerminal();
            var repository = new RecordingCharterRepositoryClient(null);
            var exitCode = await CliApplication.InvokeAsync(
                ["charter", "implement", "--product", "demo", "--no-wait"],
                CancellationToken.None,
                terminal,
                new RecordingAppConfigurationClient(
                    new ProductLocation("demo", "acme/demo", "https://demo.azconfig.io")),
                repository,
                new RecordingCharterStore());

            Assert.Equal(1, exitCode);
            Assert.Empty(repository.FilePullRequests);
            Assert.Empty(repository.Issues);
            Assert.Contains("charter for demo on main is missing", terminal.Error, StringComparison.OrdinalIgnoreCase);
        });
    }

    [Fact]
    public async Task CharterImplement_no_wait_skips_watch_and_prints_resume_command()
    {
        await WithOwnerEndpointAsync(async () =>
        {
            var terminal = NonInteractiveTerminal();
            var repository = new RecordingCharterRepositoryClient(new CharterFile(ValidCharter, "abc123"))
            {
                ConstitutionOnMain = "<!-- dsf:constitution schema_version=1 source_sha=abc123 source_ref=main -->",
            };

            var exitCode = await CliApplication.InvokeAsync(
                ["charter", "implement", "--product", "demo", "--no-wait"],
                CancellationToken.None,
                terminal,
                new RecordingAppConfigurationClient(
                    new ProductLocation("demo", "acme/demo", "https://demo.azconfig.io")),
                repository,
                new RecordingCharterStore());

            Assert.Equal(0, exitCode);
            Assert.Empty(repository.WatchedIssues);
            Assert.Contains(
                "run `dsf charter watch --product demo`",
                terminal.Output,
                StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task CharterImplement_updates_existing_constitution_with_existing_sha()
    {
        await WithOwnerEndpointAsync(async () =>
        {
            var repository = new RecordingCharterRepositoryClient(new CharterFile(ValidCharter, "newsha"))
            {
                ConstitutionOnMain = "<!-- dsf:constitution schema_version=1 source_sha=oldsha source_ref=main -->",
                ConstitutionOnMainSha = "existing-constitution-sha",
                MarkConstitutionCurrentAfterOpeningPullRequest = true,
            };

            var exitCode = await CliApplication.InvokeAsync(
                ["charter", "implement", "--product", "demo", "--no-wait"],
                CancellationToken.None,
                NonInteractiveTerminal(),
                new RecordingAppConfigurationClient(
                    new ProductLocation("demo", "acme/demo", "https://demo.azconfig.io")),
                repository,
                new RecordingCharterStore());

            Assert.Equal(0, exitCode);
            Assert.Equal("existing-constitution-sha", Assert.Single(repository.FilePullRequests).ExistingSha);
        });
    }

    [Fact]
    public async Task CharterImplement_attempts_app_assignment_before_gh_fallback()
    {
        await WithOwnerEndpointAsync(async () =>
        {
            var repository = new RecordingCharterRepositoryClient(new CharterFile(ValidCharter, "abc123"))
            {
                ConstitutionOnMain = "<!-- dsf:constitution schema_version=1 source_sha=abc123 source_ref=main -->",
                AppAssignmentSucceeds = false,
                GhAssignmentSucceeds = true,
            };

            var exitCode = await CliApplication.InvokeAsync(
                ["charter", "implement", "--product", "demo", "--no-wait"],
                CancellationToken.None,
                NonInteractiveTerminal(),
                new RecordingAppConfigurationClient(
                    new ProductLocation("demo", "acme/demo", "https://demo.azconfig.io")),
                repository,
                new RecordingCharterStore());

            Assert.Equal(0, exitCode);
            Assert.Equal(["ISSUE_node"], repository.AppAssignmentAttempts);
            Assert.Equal(["ISSUE_node"], repository.GhAssignmentAttempts);
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
    public async Task CharterWatch_uses_explicit_issue()
    {
        await WithOwnerEndpointAsync(async () =>
        {
            var appConfig = new RecordingAppConfigurationClient(
                new ProductLocation("demo", "acme/demo", "https://demo.azconfig.io"));
            var repository = new RecordingCharterRepositoryClient(null)
            {
                PullRequests =
                [
                    new RecordedCodingPullRequest(12, "https://github.test/acme/demo/pull/12", false, "OPEN"),
                ],
            };

            var exitCode = await CliApplication.InvokeAsync(
                ["charter", "watch", "--product", "demo", "--issue", "77"],
                CancellationToken.None,
                NonInteractiveTerminal(),
                appConfig,
                repository,
                new RecordingCharterStore());

            Assert.Equal(0, exitCode);
            Assert.Equal([77], repository.WatchedIssues);
            Assert.Equal([12], repository.CopilotReviewRequests);
        });
    }

    [Fact]
    public async Task CharterWatch_finds_newest_issue_when_omitted()
    {
        await WithOwnerEndpointAsync(async () =>
        {
            var appConfig = new RecordingAppConfigurationClient(
                new ProductLocation("demo", "acme/demo", "https://demo.azconfig.io"));

            var newestRepository = new RecordingCharterRepositoryClient(null)
            {
                ReadyIssues = [5, 9, 7],
                PullRequests =
                [
                    new RecordedCodingPullRequest(21, "https://github.test/acme/demo/pull/21", false, "OPEN"),
                ],
            };

            var newestExitCode = await CliApplication.InvokeAsync(
                ["charter", "watch", "--product", "demo"],
                CancellationToken.None,
                NonInteractiveTerminal(),
                appConfig,
                newestRepository,
                new RecordingCharterStore());

            Assert.Equal(0, newestExitCode);
            Assert.Equal([9], newestRepository.WatchedIssues);
        });
    }

    [Fact]
    public async Task CharterWatch_errors_when_no_ready_issue_exists()
    {
        await WithOwnerEndpointAsync(async () =>
        {
            var terminal = NonInteractiveTerminal();

            var exitCode = await CliApplication.InvokeAsync(
                ["charter", "watch", "--product", "demo"],
                CancellationToken.None,
                terminal,
                new RecordingAppConfigurationClient(
                    new ProductLocation("demo", "acme/demo", "https://demo.azconfig.io")),
                new RecordingCharterRepositoryClient(null),
                new RecordingCharterStore());

            Assert.Equal(1, exitCode);
            Assert.Contains("no open 'creation:ready' issue found", terminal.Error, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task CharterWatch_reports_newest_issue_lookup_errors()
    {
        await WithOwnerEndpointAsync(async () =>
        {
            var terminal = NonInteractiveTerminal();
            var repository = new RecordingCharterRepositoryClient(null)
            {
                NewestIssueError = new InvalidOperationException("gh auth failed"),
            };

            var exitCode = await CliApplication.InvokeAsync(
                ["charter", "watch", "--product", "demo"],
                CancellationToken.None,
                terminal,
                new RecordingAppConfigurationClient(
                    new ProductLocation("demo", "acme/demo", "https://demo.azconfig.io")),
                repository,
                new RecordingCharterStore());

            Assert.Equal(1, exitCode);
            Assert.Contains("[dsf] error: gh auth failed", terminal.Error, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task CharterWatch_marks_finished_draft_ready()
    {
        await WithOwnerEndpointAsync(async () =>
        {
            var appConfig = new RecordingAppConfigurationClient(
                new ProductLocation("demo", "acme/demo", "https://demo.azconfig.io"));

            var draftTerminal = NonInteractiveTerminal();
            var draftRepository = new RecordingCharterRepositoryClient(null)
            {
                PullRequests =
                [
                    new RecordedCodingPullRequest(31, "https://github.test/acme/demo/pull/31", true, "OPEN"),
                ],
                FinishedPullRequests = [31],
            };
            var draftExitCode = await CliApplication.InvokeAsync(
                ["charter", "watch", "--product", "demo", "--issue", "3"],
                CancellationToken.None,
                draftTerminal,
                appConfig,
                draftRepository,
                new RecordingCharterStore());

            Assert.Equal(0, draftExitCode);
            Assert.Equal([31], draftRepository.MarkedReadyPullRequests);
            Assert.Equal([31], draftRepository.CopilotReviewRequests);
        });
    }

    [Fact]
    public async Task CharterWatch_reuses_existing_review_request()
    {
        await WithOwnerEndpointAsync(async () =>
        {
            var appConfig = new RecordingAppConfigurationClient(
                new ProductLocation("demo", "acme/demo", "https://demo.azconfig.io"));
            var reviewedTerminal = NonInteractiveTerminal();
            var reviewedRepository = new RecordingCharterRepositoryClient(null)
            {
                PullRequests =
                [
                    new RecordedCodingPullRequest(32, "https://github.test/acme/demo/pull/32", false, "OPEN"),
                ],
                ExistingCopilotReviewRequests = [32],
            };
            var reviewedExitCode = await CliApplication.InvokeAsync(
                ["charter", "watch", "--product", "demo", "--issue", "3"],
                CancellationToken.None,
                reviewedTerminal,
                appConfig,
                reviewedRepository,
                new RecordingCharterStore());

            Assert.Equal(0, reviewedExitCode);
            Assert.Empty(reviewedRepository.CopilotReviewRequests);
            Assert.Contains("Copilot review already requested", reviewedTerminal.Output, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task CharterWatch_handles_closed_pull_request()
    {
        await WithOwnerEndpointAsync(async () =>
        {
            var appConfig = new RecordingAppConfigurationClient(
                new ProductLocation("demo", "acme/demo", "https://demo.azconfig.io"));
            var closedTerminal = NonInteractiveTerminal();
            var closedRepository = new RecordingCharterRepositoryClient(null)
            {
                PullRequests =
                [
                    new RecordedCodingPullRequest(33, "https://github.test/acme/demo/pull/33", false, "CLOSED"),
                ],
            };
            var closedExitCode = await CliApplication.InvokeAsync(
                ["charter", "watch", "--product", "demo", "--issue", "3"],
                CancellationToken.None,
                closedTerminal,
                appConfig,
                closedRepository,
                new RecordingCharterStore());

            Assert.Equal(0, closedExitCode);
            Assert.Contains("nothing to review", closedTerminal.Output, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task CharterWatch_times_out_waiting_for_pull_request()
    {
        await WithOwnerEndpointAsync(async () =>
        {
            var appConfig = new RecordingAppConfigurationClient(
                new ProductLocation("demo", "acme/demo", "https://demo.azconfig.io"));
            var timeoutTerminal = NonInteractiveTerminal();
            var timeoutRepository = new RecordingCharterRepositoryClient(null);
            var timeoutExitCode = await CliApplication.InvokeAsync(
                ["charter", "watch", "--product", "demo", "--issue", "3", "--timeout", "0.01", "--poll-interval", "0.01"],
                CancellationToken.None,
                timeoutTerminal,
                appConfig,
                timeoutRepository,
                new RecordingCharterStore());

            Assert.Equal(2, timeoutExitCode);
            Assert.Contains("still building", timeoutTerminal.Output, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task CharterWatch_retries_transient_pull_request_errors()
    {
        await WithOwnerEndpointAsync(async () =>
        {
            var appConfig = new RecordingAppConfigurationClient(
                new ProductLocation("demo", "acme/demo", "https://demo.azconfig.io"));
            var retryRepository = new RecordingCharterRepositoryClient(null)
            {
                TransientFailuresBeforePullRequest = 1,
                PullRequests =
                [
                    new RecordedCodingPullRequest(34, "https://github.test/acme/demo/pull/34", false, "OPEN"),
                ],
            };
            var retryExitCode = await CliApplication.InvokeAsync(
                ["charter", "watch", "--product", "demo", "--issue", "3", "--timeout", "1", "--poll-interval", "0.01"],
                CancellationToken.None,
                NonInteractiveTerminal(),
                appConfig,
                retryRepository,
                new RecordingCharterStore());

            Assert.Equal(0, retryExitCode);
            Assert.Equal(2, retryRepository.PullRequestLookups);
            Assert.Equal([34], retryRepository.CopilotReviewRequests);
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
    public List<RecordedFilePullRequest> FilePullRequests { get; } = [];
    public List<RecordedIssue> Issues { get; } = [];
    public List<int> ReadyIssues { get; init; } = [];
    public List<int> WatchedIssues { get; } = [];
    public List<RecordedCodingPullRequest> PullRequests { get; init; } = [];
    public List<int> FinishedPullRequests { get; init; } = [];
    public List<int> ExistingCopilotReviewRequests { get; init; } = [];
    public List<int> MarkedReadyPullRequests { get; } = [];
    public List<int> CopilotReviewRequests { get; } = [];
    public string? ConstitutionOnMain { get; set; }
    public bool MarkConstitutionCurrentAfterOpeningPullRequest { get; init; }
    public string? ConstitutionOnMainSha { get; init; }
    public bool AppAssignmentSucceeds { get; init; } = true;
    public bool GhAssignmentSucceeds { get; init; } = true;
    public Exception? NewestIssueError { get; init; }
    public int TransientFailuresBeforePullRequest { get; init; }
    public int PullRequestLookups { get; private set; }
    public List<string> AppAssignmentAttempts { get; } = [];
    public List<string> GhAssignmentAttempts { get; } = [];

    public Task<CharterFile?> ReadAsync(
        string repository,
        string path,
        string? reference,
        CancellationToken cancellationToken)
    {
        Reads.Add((repository, path, reference));
        if (path == ".specify/memory/constitution.md")
        {
            return Task.FromResult<CharterFile?>(ConstitutionOnMain is null
                ? null
                : new CharterFile(ConstitutionOnMain, ConstitutionOnMainSha ?? "constitution-sha"));
        }

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

    public Task<CharterPullRequest?> LatestPullRequestWithHeadPrefixAsync(
        string repository,
        string headPrefix,
        CancellationToken cancellationToken) =>
        Task.FromResult<CharterPullRequest?>(null);

    public Task<string> OpenFilePullRequestAsync(
        string repository,
        string path,
        string content,
        string branch,
        string title,
        string body,
        string message,
        bool enableAutoMerge,
        string? existingSha,
        CancellationToken cancellationToken)
    {
        FilePullRequests.Add(new RecordedFilePullRequest(
            repository, path, content, branch, title, body, message, enableAutoMerge, existingSha));
        if (MarkConstitutionCurrentAfterOpeningPullRequest)
        {
            ConstitutionOnMain = content;
        }

        return Task.FromResult("https://github.test/acme/demo/pull/2");
    }

    public Task<CharterIssue> CreateIssueAsync(
        string repository,
        string title,
        string body,
        IReadOnlyList<string> labels,
        CancellationToken cancellationToken)
    {
        Issues.Add(new RecordedIssue(repository, title, body, labels));
        return Task.FromResult(new CharterIssue("https://github.test/acme/demo/issues/9", "ISSUE_node"));
    }

    public Task<bool> AssignCopilotWithAppAsync(
        string repository,
        string issueNodeId,
        CancellationToken cancellationToken)
    {
        AppAssignmentAttempts.Add(issueNodeId);
        return Task.FromResult(AppAssignmentSucceeds);
    }

    public Task<bool> AssignCopilotWithGhAsync(
        string repository,
        string issueNodeId,
        CancellationToken cancellationToken)
    {
        GhAssignmentAttempts.Add(issueNodeId);
        return Task.FromResult(GhAssignmentSucceeds);
    }

    public Task<int?> NewestReadyIssueAsync(
        string repository,
        string label,
        CancellationToken cancellationToken)
    {
        if (NewestIssueError is not null)
        {
            throw NewestIssueError;
        }

        return Task.FromResult<int?>(ReadyIssues.Count == 0 ? null : ReadyIssues.Max());
    }

    public Task<CodingAgentPullRequest?> FindCodingAgentPullRequestAsync(
        string repository,
        int issueNumber,
        CancellationToken cancellationToken)
    {
        WatchedIssues.Add(issueNumber);
        PullRequestLookups++;
        if (PullRequestLookups <= TransientFailuresBeforePullRequest)
        {
            throw new HttpRequestException("transient");
        }

        var pullRequest = PullRequests.FirstOrDefault();
        return Task.FromResult<CodingAgentPullRequest?>(pullRequest is null
            ? null
            : new CodingAgentPullRequest(
                pullRequest.Number,
                pullRequest.Url,
                pullRequest.IsDraft,
                pullRequest.State));
    }

    public Task<bool> HasCopilotReviewRequestAsync(
        string repository,
        int pullRequestNumber,
        CancellationToken cancellationToken) =>
        Task.FromResult(ExistingCopilotReviewRequests.Contains(pullRequestNumber));

    public Task RequestCopilotReviewAsync(
        string repository,
        string pullRequestUrl,
        CancellationToken cancellationToken)
    {
        CopilotReviewRequests.Add(PullRequests.Single(pr => pr.Url == pullRequestUrl).Number);
        return Task.CompletedTask;
    }

    public Task<bool> AgentWorkFinishedAsync(
        string repository,
        int pullRequestNumber,
        CancellationToken cancellationToken) =>
        Task.FromResult(FinishedPullRequests.Contains(pullRequestNumber));

    public Task MarkPullRequestReadyAsync(
        string repository,
        int pullRequestNumber,
        CancellationToken cancellationToken)
    {
        MarkedReadyPullRequests.Add(pullRequestNumber);
        return Task.CompletedTask;
    }
}

internal sealed record RecordedFilePullRequest(
    string Repository,
    string Path,
    string Content,
    string Branch,
    string Title,
    string Body,
    string Message,
    bool EnableAutoMerge,
    string? ExistingSha);

internal sealed record RecordedIssue(string Repository, string Title, string Body, IReadOnlyList<string> Labels);

internal sealed record RecordedCodingPullRequest(int Number, string Url, bool IsDraft, string State);

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
