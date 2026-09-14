using Dsf.Cli;
using Xunit;

namespace Dsf.Cli.Tests;

public sealed class LiveNewProgressTests
{
    [Fact]
    public async Task New_wires_progress_before_provisioning_and_registration_operations()
    {
        var root = Path.Combine(Path.GetTempPath(), "dsf-cli-tests", Guid.NewGuid().ToString("N"));
        var terminal = new ScriptedTerminal(new TerminalCapabilities(false, false, false), []);
        var github = new RecordingGitHubProvisioningClient
        {
            OnEnsureRepository = () =>
            {
                Assert.Contains("Preparing live provisioning for product progress-demo", terminal.Output);
                Assert.Contains("Ensuring GitHub repository acme/progress-demo", terminal.Output);
            },
        };
        var azure = new RecordingAzureProvisioningClient
        {
            OnEnsureResourceGroup = () => Assert.Contains("Ensuring Azure resource group", terminal.Output),
        };
        var appConfig = new RecordingAppConfigurationClient
        {
            OnWrite = method =>
            {
                var expected = method switch
                {
                    nameof(IAppConfigurationClient.SeedProductRecordAsync) => "Writing product record",
                    nameof(IAppConfigurationClient.SeedSourceAgentRosterAsync) => "Writing source-agent settings",
                    nameof(IAppConfigurationClient.PublishRuntimeIndexAsync) => "Publishing product runtime index",
                    _ => throw new InvalidOperationException($"Unexpected write: {method}"),
                };
                Assert.Contains(expected, terminal.Output);
            },
        };

        try
        {
            var exitCode = await CliApplication.InvokeAsync(
                [
                    "new", "--product", "progress-demo", "--owner", "acme",
                    "--github-app-id", "7", "--github-installation-id", "42",
                    "--owner-appconfig-endpoint", "https://owner.azconfig.io", "--config-root", root,
                ],
                CancellationToken.None, terminal, github, azure, appConfig,
                new RecordingCharterRepositoryClient(null));

            Assert.Equal(0, exitCode);
            Assert.Empty(terminal.Error);
            Assert.Contains("Completed: Saving instance manifest for progress-demo", terminal.Output);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }
}
