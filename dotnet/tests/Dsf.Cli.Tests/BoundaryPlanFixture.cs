using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Dsf.Cli;
using Xunit;
using ReviewFixture = Dsf.Cli.Tests.RepositoryReviewFixture;

namespace Dsf.Cli.Tests;

internal sealed class BoundaryPlanFixture : IAzureCliRunner
{
    public const string Tenant = "11111111-1111-1111-1111-111111111111";
    public const string Subscription = "22222222-2222-2222-2222-222222222222";
    public const string Principal = "33333333-3333-3333-3333-333333333333";
    public const string Root = "/subscriptions/" + Subscription;
    public const string Api = Root + "/resourceGroups/app-a/providers/Microsoft.Web/sites/api";
    public const string Data = Root + "/resourceGroups/app-b/providers/Microsoft.Storage/storageAccounts/data";
    public const string Logs = Root + "/resourceGroups/logs/providers/Microsoft.OperationalInsights/workspaces/app";
    public ReviewFixture Repository { get; } = new();
    public List<IReadOnlyList<string>> Calls { get; } = [];
    public Dictionary<string, JsonNode> Resources { get; } = new(StringComparer.OrdinalIgnoreCase)
    {
        [Api] = Resource(Api, "Microsoft.Web/sites"),
        [Data] = Resource(Data, "Microsoft.Storage/storageAccounts"),
        [Logs] = Resource(Logs, "Microsoft.OperationalInsights/workspaces"),
        [Root + "/resourceGroups/app-a/providers/Microsoft.Web/sites/sibling"] =
            Resource(Root + "/resourceGroups/app-a/providers/Microsoft.Web/sites/sibling", "Microsoft.Web/sites"),
    };
    public Dictionary<string, AzureCliInvocationResult> Overrides { get; } = [];
    public string? InputTextOverride { get; set; }
    public Func<string>? OwnerEntries { get; set; }
    public string? AppConfigActor { get; set; }
    public string DefaultOutput { get; set; } = "json";
    public JsonObject Input { get; } = new()
    {
        ["schemaVersion"] = 1, ["repository"] = "acme/old-name",
        ["ownerAppConfigEndpoint"] = "https://owner.azconfig.io",
        ["ownerKeyVaultUri"] = "https://owner.vault.azure.net/",
        ["productKey"] = "widget", ["tenantId"] = Tenant, ["subscriptionId"] = Subscription,
        ["applicationEnvironment"] = "production",
        ["applicationResourceIds"] = new JsonArray(Api, Data),
        ["evidenceBackends"] = new JsonArray(new JsonObject { ["resourceId"] = Logs, ["tables"] = new JsonArray("AppRequests") }),
        ["factoryResourceGroup"] = "widget-decide", ["factoryLocation"] = "swedencentral",
        ["modelResourceIds"] = new JsonArray(), ["automationOwner"] = "acme/platform",
        ["automationDeclarations"] = new JsonObject
        {
            ["ProposalIssue"] = "Clear", ["CharterPullRequest"] = "Clear", ["LabelCreation"] = "Clear",
        },
        ["boundaryReview"] = null,
    };

    public void SetLegacyRegistration(string repository, string product = "other")
    {
        Overrides["appconfig kv"] = new(0, JsonSerializer.Serialize(new[]
        {
            new { key = "GITHUB_REPOSITORY", label = product, etag = "repository-revision", value = repository },
            new { key = "AZURE_APPCONFIG_ENDPOINT", label = product, etag = "endpoint-revision", value = "https://other.azconfig.io" },
        }), "");
    }

    public async Task<(int Exit, JsonNode Plan, ScriptedTerminal Terminal)> ReviewAsync()
    {
        var (_, draft, _) = await PlanAsync();
        Input["boundaryReview"] = new JsonObject
        {
            ["selectionFingerprint"] = (string)draft["selectionFingerprint"]!,
            ["accountableOwner"] = "acme/platform", ["dedicated"] = true, ["crossOwnerExclusive"] = true,
        };
        return await PlanAsync();
    }

    public async Task<(int Exit, JsonNode Plan, ScriptedTerminal Terminal)> PlanAsync(params string[] extra)
    {
        Repository.BoundaryAzureRunner = this;
        var path = Path.GetTempFileName();
        var terminal = new ScriptedTerminal(new(false, false, false), []);
        try
        {
            await File.WriteAllTextAsync(path, InputTextOverride ?? Input.ToJsonString());
            var exit = await Repository.InvokeCommandAsync(terminal,
                ["onboard", "decide", "plan", "--input", path, "--json", .. extra]);
            AssertReadOnly();
            return (exit, string.IsNullOrWhiteSpace(terminal.Output) ? new JsonObject() : JsonNode.Parse(terminal.Output)!, terminal);
        }
        finally
        {
            File.Delete(path);
        }
    }

    public Task<AzureCliInvocationResult> RunAsync(IReadOnlyList<string> args, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        Calls.Add(args.ToArray());
        var outputIndex = args.ToList().FindIndex(argument => argument is "--output" or "-o");
        var output = outputIndex >= 0 ? args[outputIndex + 1] : DefaultOutput;
        if (output != "json")
            return Task.FromResult(new AzureCliInvocationResult(0, output == "yaml"
                ? "accessToken: fixture-output-token-do-not-print\nsubscription: " + Subscription
                : "AccessToken                         Subscription\n----------------------------------  ------------------------------------\n"
                    + "fixture-output-token-do-not-print   " + Subscription, ""));
        var operation = string.Join(' ', args.Take(2));
        var key = operation == "rest --method" ? args[args.IndexOf("--url") + 1] : operation;
        if (operation == "appconfig kv" && OwnerEntries is not null)
            return Task.FromResult(new AzureCliInvocationResult(0, OwnerEntries(), ""));
        if (Overrides.TryGetValue(key, out var response))
        {
            return Task.FromResult(response);
        }

        object body = operation switch
        {
            "account list" => new[] { new { id = Subscription, tenantId = Tenant, name = "Application", state = "Enabled", user = new { name = "operator@example.test", type = "user" } } },
            "account get-access-token" => new
            {
                tenant = Tenant, subscription = Subscription,
                accessToken = "header." + Convert.ToBase64String(Encoding.UTF8.GetBytes(
                    JsonSerializer.Serialize(new
                    {
                        oid = args.Contains("https://azconfig.io") ? AppConfigActor ?? Principal : Principal,
                        tid = Tenant, exp = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds(),
                    }))).TrimEnd('=').Replace('+', '-').Replace('/', '_') + ".signature",
            },
            "resource list" => Resources.Values.Select(resource => new
            {
                id = resource["id"], type = resource["type"], location = resource["location"], tags = resource["tags"],
            }).ToArray(),
            "resource show" => Resources[args[args.IndexOf("--ids") + 1]],
            "group list" => Array.Empty<object>(),
            "appconfig kv" => Array.Empty<object>(),
            "rest --method" when key.Contains("/tables?") => new { value = new[] { new { name = "AppRequests", properties = new { plan = "Analytics" } } } },
            "rest --method" when key.Contains("/permissions?") => new { value = new[] { new { actions = new[] { "*/read", "Microsoft.Resources/deployments/*" }, notActions = Array.Empty<string>(), dataActions = Array.Empty<string>(), notDataActions = Array.Empty<string>() } } },
            "rest --method" => Resources[new Uri(key).AbsolutePath],
            _ => throw new InvalidOperationException($"Unexpected Azure operation: {operation}"),
        };
        return Task.FromResult(new AzureCliInvocationResult(0, JsonSerializer.Serialize(body), ""));
    }

    public void AssertReadOnly()
    {
        Repository.AssertReadOnly();
        Assert.All(Calls, args =>
        {
            var operation = string.Join(' ', args.Take(2));
            Assert.Contains(operation, new[] { "account list", "account get-access-token", "resource list", "resource show", "group list", "appconfig kv", "rest --method" });
            if (operation == "rest --method") Assert.Equal("GET", args[2]);
            if (operation == "appconfig kv") Assert.Equal("list", args[2]);
            Assert.DoesNotContain("set", args);
        });
    }

    public static JsonNode Resource(string id, string type) => new JsonObject
    {
        ["id"] = id, ["type"] = type, ["location"] = "swedencentral",
        ["tags"] = new JsonObject { ["environment"] = "production" },
        ["properties"] = new JsonObject { ["customerId"] = "44444444-4444-4444-4444-444444444444" },
    };
}

internal static class BoundaryArgumentExtensions
{
    public static int IndexOf(this IReadOnlyList<string> values, string value) =>
        values.Select((item, index) => (item, index)).Single(pair => pair.item == value).index;
}
