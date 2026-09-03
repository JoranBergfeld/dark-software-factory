using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Dsf.Core.Runtime;
using Dsf.FeatureCouncil.Conveyor;
using Microsoft.AspNetCore.Builder;
using Xunit;

namespace Dsf.Runtime.Tests;

/// <summary>
/// What an operator is told about a dry run and about a run that failed: the
/// preview must name the issues the line would have filed (and nothing must
/// actually be filed), and a failed station must be reported as the cause with a
/// non-zero exit -- even when telemetry or persistence failed on the way out.
/// </summary>
public sealed class ConveyorDryRunReportingTests
{
    private static readonly IReadOnlyDictionary<string, string?> FullEnvironment = new Dictionary<string, string?>
    {
        ["DSF_PRODUCT"] = "acme",
        ["AZURE_APPCONFIG_ENDPOINT"] = "https://appconfig.example",
        ["AZURE_COSMOS_ENDPOINT"] = "https://cosmos.example",
        ["AZURE_OPENAI_ENDPOINT"] = "https://openai.example",
        ["AZURE_OPENAI_DEPLOYMENT"] = "gpt-deploy",
        ["AZURE_OPENAI_EMBEDDING_DEPLOYMENT"] = "embed-deploy",
    };

    private static readonly RuntimeSettings Settings = new(
        Product: "acme",
        AppConfigEndpoint: "https://appconfig.example",
        KeyVaultUri: "",
        AppInsightsConnectionString: "",
        CosmosEndpoint: "https://cosmos.example",
        OpenAiEndpoint: "https://openai.example",
        OpenAiDeployment: "gpt-deploy",
        OpenAiEmbeddingDeployment: "embed-deploy",
        GitHubAppId: "",
        GitHubInstallationId: "",
        GitHubAppPrivateKeySecret: "",
        GitHubRepository: "acme/acme");

    private static readonly EvidenceItem SentryEvidence =
        new("sentry", "SENTRY-1", "checkout 500s spiked after release 4.2");

    private static async Task<string> WriteSignalAsync(string json)
    {
        var path = Path.Combine(Path.GetTempPath(), $"dsf-signal-{Guid.NewGuid():n}.json");
        await File.WriteAllTextAsync(path, json);
        return path;
    }

    private static async Task<(int ExitCode, string Stdout, string Stderr)> InvokeAsync(
        RuntimeDependencies dependencies, params string[] args)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var exitCode = await RuntimeCliApplication.InvokeAsync(
            args, FullEnvironment, stdout, stderr, dependencies, CancellationToken.None);
        return (exitCode, stdout.ToString(), stderr.ToString());
    }

    [Fact]
    public async Task A_dry_run_prints_the_issues_it_would_have_filed_and_files_none()
    {
        var filer = new RecordingIssueFiler();
        var dependencies = TestDependencies.Build(
            evidenceGatherers: [new ScriptedEvidenceGatherer("sentry", SentryEvidence)],
            issueFiler: filer);
        var path = await WriteSignalAsync("""{"product_hints": "acme", "source_kinds": ["sentry"]}""");
        try
        {
            var (exitCode, stdout, stderr) = await InvokeAsync(dependencies, "run", "--signal", path, "--dry-run");

            Assert.Equal(0, exitCode);
            Assert.Equal(string.Empty, stderr);
            Assert.Contains("status=previewed", stdout, StringComparison.Ordinal);
            Assert.Contains("would file", stdout, StringComparison.Ordinal);
            Assert.Contains("[sentry] checkout 500s spiked after release 4.2", stdout, StringComparison.Ordinal);
            Assert.Empty(filer.Filed);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task A_failed_station_is_reported_as_the_cause_and_exits_non_zero()
    {
        var dependencies = TestDependencies.Build(
            evidenceGatherers: [new ScriptedEvidenceGatherer("sentry", SentryEvidence)],
            modelClient: new ThrowingModelClient("azure openai returned 429"),
            tracer: new UnreachableTracer("app insights ingestion refused the connection"));
        var path = await WriteSignalAsync("""{"product_hints": "acme", "source_kinds": ["sentry"]}""");
        try
        {
            var (exitCode, _, stderr) = await InvokeAsync(dependencies, "run", "--signal", path, "--dry-run");

            Assert.Equal(1, exitCode);
            Assert.Contains("s3_synthesis", stderr, StringComparison.Ordinal);
            Assert.Contains("azure openai returned 429", stderr, StringComparison.Ordinal);
            Assert.DoesNotContain("app insights", stderr, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task The_orchestrator_preview_endpoint_answers_with_the_issues_it_would_have_filed()
    {
        var filer = new RecordingIssueFiler();
        var dependencies = TestDependencies.Build(
            evidenceGatherers: [new ScriptedEvidenceGatherer("sentry", SentryEvidence)],
            issueFiler: filer);
        await using var app = RuntimeVerbs.BuildOrchestratorHost(Settings, dependencies, "127.0.0.1", 0);
        await app.StartAsync();
        try
        {
            using var client = new HttpClient { BaseAddress = new Uri(app.Urls.First()) };
            using var response = await client.PostAsync(
                "/run",
                new StringContent(
                    """{"product_hints": "acme", "source_kinds": ["sentry"]}""", Encoding.UTF8, "application/json"));

            response.EnsureSuccessStatusCode();
            var summary = await response.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("previewed", summary.GetProperty("status").GetString());
            var previews = summary.GetProperty("previewedIssues").EnumerateArray().ToList();
            var preview = Assert.Single(previews);
            Assert.Equal("[sentry] checkout 500s spiked after release 4.2", preview.GetProperty("title").GetString());
            Assert.Contains(
                "ready-for-agent",
                preview.GetProperty("labels").EnumerateArray().Select(label => label.GetString()));
            Assert.Empty(summary.GetProperty("filedIssues").EnumerateArray());
            Assert.Empty(filer.Filed);
        }
        finally
        {
            await app.StopAsync();
        }
    }

    [Fact]
    public async Task A_persisted_previewed_run_records_what_it_would_have_filed_and_why_it_failed()
    {
        var gateway = new RecordingRunDocumentGateway();
        var store = new CosmosRunStore("https://cosmos.example", "dsf", "runs", "acme", gateway);
        var run = new ConveyorRun { ProductHints = ["acme"], SourceKinds = ["sentry"], DryRun = true };
        run.PreviewedIssues.Add(new IssuePreview("[sentry] checkout 500s", "abc123:sentry", ["ready-for-agent"]));
        run.FailureReason = "station 's3_synthesis' failed (InvalidOperationException): azure openai returned 429";

        await store.SaveAsync(run, "s7_filing", CancellationToken.None);

        using var document = JsonDocument.Parse(gateway.Upserts[^1]);
        var preview = Assert.Single(document.RootElement.GetProperty("previewedIssues").EnumerateArray());
        Assert.Equal("[sentry] checkout 500s", preview.GetProperty("title").GetString());
        Assert.Equal("abc123:sentry", preview.GetProperty("intentKey").GetString());
        Assert.Contains("azure openai returned 429", document.RootElement.GetProperty("failureReason").GetString()!);
    }

    private sealed class RecordingRunDocumentGateway : ICosmosDocumentGateway
    {
        public List<string> Upserts { get; } = [];

        public Task UpsertAsync(
            string endpoint, string database, string container, string partitionKey, string id, string json,
            CancellationToken cancellationToken)
        {
            this.Upserts.Add(json);
            return Task.CompletedTask;
        }

        public Task<string?> ReadAsync(
            string endpoint, string database, string container, string partitionKey, string id,
            CancellationToken cancellationToken) =>
            Task.FromResult<string?>(null);
    }
}
