using System.Text;
using System.Text.Json;
using Dsf.Core.Onboarding;

namespace Dsf.Cli;

internal sealed class BoundaryInspectionException(string operation)
    : Exception($"Azure {operation} inspection is unavailable or incomplete; provider output was withheld.");

internal sealed class AzureBoundaryReader(IAzureCliRunner runner)
{
    private const string Arm = "https://management.azure.com";

    public IAzureCliRunner ForSubscription(string subscription) => new SubscriptionRunner(runner, subscription);

    private sealed class SubscriptionRunner(IAzureCliRunner inner, string subscription) : IAzureCliRunner
    {
        public Task<AzureCliInvocationResult> RunAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken) =>
            inner.RunAsync([.. arguments, "--subscription", subscription], cancellationToken);
    }

    public async Task<IReadOnlyList<AzureSubscriptionChoice>> SubscriptionsAsync(CancellationToken cancellationToken)
    {
        var body = await ReadAsync(["account", "list", "--all"], cancellationToken);
        return Parse("account list", () => body.EnumerateArray()
            .Where(a => Text(a, "state") == "Enabled")
            .Select(a => new AzureSubscriptionChoice(
                Guid.Parse(Text(a, "tenantId")).ToString(), Guid.Parse(Text(a, "id")).ToString(), Text(a, "name")))
            .OrderBy(a => a.TenantId, StringComparer.Ordinal).ThenBy(a => a.SubscriptionId, StringComparer.Ordinal).ToArray());
    }

    public async Task<IReadOnlyList<BoundaryResource>> InventoryAsync(string subscription, CancellationToken cancellationToken)
    {
        var body = await ReadAsync(["resource", "list", "--subscription", subscription], cancellationToken);
        return Parse("resource inventory", () => body.EnumerateArray().Select(Resource).ToArray());
    }

    public async Task<AzureBoundaryDiscovery> InspectAsync(
        ApplicationBoundarySelection selection, CancellationToken cancellationToken)
    {
        var findings = new List<BoundaryPlanFinding>();
        var resources = new List<BoundaryResource>();
        var permissions = new List<AzurePermissionObservation>();
        var topology = new List<BoundaryResource>();
        var tables = new List<EvidenceTableObservation>();
        string? actor = null;
        try
        {
            var accounts = await SubscriptionsAsync(cancellationToken);
            if (!accounts.Any(a => a.TenantId == selection.TenantId && a.SubscriptionId == selection.SubscriptionId))
            {
                findings.Add(new("azure-account", BoundaryPlanState.Blocked,
                    "Selected tenant/subscription is not an enabled account of the operator Azure CLI identity.",
                    "Azure operator", "Choose one accessible tenant/subscription explicitly; DSF does not switch accounts or log in."));
                return Result();
            }

            var token = await ReadAsync(["account", "get-access-token", "--subscription", selection.SubscriptionId,
                "--resource", Arm + "/"], cancellationToken);
            actor = Parse("operator identity", () =>
            {
                if (Guid.Parse(Text(token, "tenant")).ToString() != selection.TenantId
                    || Guid.Parse(Text(token, "subscription")).ToString() != selection.SubscriptionId)
                    throw new FormatException();
                var part = Text(token, "accessToken").Split('.')[1].Replace('-', '+').Replace('_', '/');
                using var claims = JsonDocument.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(part.PadRight((part.Length + 3) / 4 * 4, '='))));
                if (Guid.Parse(Text(claims.RootElement, "tid")).ToString() != selection.TenantId) throw new FormatException();
                return Guid.Parse(Text(claims.RootElement, "oid")).ToString();
            });
            var inventory = await InventoryAsync(selection.SubscriptionId, cancellationToken);
            var groupBody = await ReadAsync(["group", "list", "--subscription", selection.SubscriptionId], cancellationToken);
            var groupExists = Parse("factory resource group", () => groupBody.EnumerateArray().Any(g =>
                Text(g, "id").TrimEnd('/').Equals($"/subscriptions/{selection.SubscriptionId}/resourceGroups/{selection.FactoryResourceGroup}",
                    StringComparison.OrdinalIgnoreCase)));
            if (groupExists)
                findings.Add(new("factory-group", BoundaryPlanState.Blocked,
                    "The proposed new factory resource group already exists. Names/tags do not authorize adoption or deployment into existing assets.",
                    "Azure operator", "Choose a genuinely new isolated factory group; reconcile existing factory identity outside this preview."));
            foreach (var id in selection.ApplicationResourceIds.Concat(selection.EvidenceBackends.Select(b => b.ResourceId))
                         .Concat(selection.ModelResourceIds).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.Ordinal))
            {
                if (!inventory.Any(r => r.Id.Equals(id, StringComparison.OrdinalIgnoreCase)))
                {
                    findings.Add(new("selected-resource", BoundaryPlanState.Unknown,
                        $"Selected resource {id} was not visible in subscription inventory; absence is not an unclaimed or nonexistent resource.",
                        "resource administrator", "Establish read access and current identity without widening the selection."));
                    continue;
                }

                try
                {
                    var body = await ReadAsync(["resource", "show", "--ids", id, "--subscription", selection.SubscriptionId], cancellationToken);
                    var resource = Parse("selected resource", () => Resource(body));
                    if (!resource.Id.Equals(id, StringComparison.OrdinalIgnoreCase)) throw new BoundaryInspectionException("resource identity");
                    resources.Add(resource);
                }
                catch (BoundaryInspectionException exception)
                {
                    findings.Add(Unavailable("selected-resource", exception.Message));
                }
            }

            foreach (var component in inventory.Where(r => r.Type == "microsoft.insights/components"))
            {
                try
                {
                    var observed = resources.FirstOrDefault(r => r.Id == component.Id);
                    if (observed is null)
                    {
                        var body = await ReadAsync(["resource", "show", "--ids", component.Id,
                            "--subscription", selection.SubscriptionId], cancellationToken);
                        observed = Parse("workspace topology", () => Resource(body));
                        if (observed.Id != component.Id) throw new BoundaryInspectionException("workspace topology identity");
                    }
                    if (selection.ApplicationResourceIds.Contains(observed.Id, StringComparer.OrdinalIgnoreCase)
                        || selection.EvidenceBackends.Any(b => b.ResourceId.Equals(observed.BackingWorkspaceResourceId, StringComparison.OrdinalIgnoreCase)))
                        topology.Add(observed);
                }
                catch (BoundaryInspectionException exception)
                {
                    findings.Add(Unavailable("workspace-topology", exception.Message));
                }
            }
            foreach (var backend in selection.EvidenceBackends)
            {
                var resource = resources.FirstOrDefault(r => r.Id.Equals(backend.ResourceId, StringComparison.OrdinalIgnoreCase));
                if (resource?.Type != "microsoft.operationalinsights/workspaces") continue;
                try
                {
                    var values = await GetValuesAsync(resource.Id + "/tables?api-version=2022-10-01", cancellationToken);
                    foreach (var table in values)
                    {
                        var name = Parse("workspace table name", () => Text(table, "name"));
                        if (!backend.Tables.Contains(name, StringComparer.OrdinalIgnoreCase)) continue;
                        tables.Add(Parse("workspace table", () => new EvidenceTableObservation(resource.Id,
                            name, Text(table.GetProperty("properties"), "plan"))));
                    }
                }
                catch (BoundaryInspectionException exception)
                {
                    findings.Add(Unavailable("evidence-tables", exception.Message));
                }
            }

            foreach (var scope in new[] { "/subscriptions/" + selection.SubscriptionId }.Concat(resources.Select(r => r.Id)))
            {
                try
                {
                    var values = await GetValuesAsync(scope + "/providers/Microsoft.Authorization/permissions?api-version=2022-04-01", cancellationToken);
                    permissions.AddRange(Parse("permissions", () => values.Select(p => new AzurePermissionObservation(
                        scope, Strings(p, "actions"), Strings(p, "notActions"),
                        OptionalStrings(p, "dataActions"), OptionalStrings(p, "notDataActions"))).ToArray()));
                }
                catch (BoundaryInspectionException exception)
                {
                    findings.Add(Unavailable("azure-authority", exception.Message));
                }
            }
        }
        catch (BoundaryInspectionException exception)
        {
            findings.Add(Unavailable("azure-discovery", exception.Message));
        }
        findings.Add(new("identity-binding", BoundaryPlanState.Reviewed,
            "Discovery and deployment rights are not delegation or effective workload/network access. "
            + "Factory identities are symbolic, not existing principal IDs. Recheck principal/role/scope/condition bindings before any later grant.",
            "Azure grantor / network administrator", "Review exact grants and verify the deployed workload path after principal creation."));
        return Result();

        AzureBoundaryDiscovery Result() => new(selection.TenantId, selection.SubscriptionId, actor, resources, permissions, findings)
        {
            InspectedTopology = topology, EvidenceTables = tables,
        };
    }

    internal async Task<IReadOnlyList<JsonElement>> GetValuesAsync(string path, CancellationToken cancellationToken)
    {
        var values = new List<JsonElement>();
        var url = Arm + path;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        while (true)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != "https" || uri.Host != "management.azure.com"
                || !uri.IsDefaultPort || uri.UserInfo.Length != 0 || uri.Fragment.Length != 0
                || !uri.AbsolutePath.Equals(path.Split('?')[0], StringComparison.OrdinalIgnoreCase)
                || !seen.Add(url) || seen.Count > 100)
                throw new BoundaryInspectionException("paginated response");
            var body = await ReadAsync(["rest", "--method", "GET", "--url", url, "--subscription", path.Split('/')[2]], cancellationToken);
            values.AddRange(Parse("paginated response", () => body.GetProperty("value").EnumerateArray().Select(v => v.Clone()).ToArray()));
            if (!body.TryGetProperty("nextLink", out var next) || next.ValueKind == JsonValueKind.Null) return values;
            url = Parse("pagination", () => Text(body, "nextLink"));
        }
    }

    internal async Task<JsonElement> ReadAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        try
        {
            var result = await runner.RunAsync([.. arguments, "--output", "json", "--only-show-errors"], cancellationToken);
            if (result.ExitCode != 0) throw new BoundaryInspectionException(string.Join(' ', arguments.Take(2)));
            using var document = JsonDocument.Parse(result.StandardOutput, new JsonDocumentOptions { AllowDuplicateProperties = false });
            return document.RootElement.Clone();
        }
        catch (Exception exception) when (exception is JsonException or IOException
            or System.ComponentModel.Win32Exception or InvalidOperationException
            || exception is OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            throw new BoundaryInspectionException(string.Join(' ', arguments.Take(2)));
        }
    }

    internal static BoundaryResource Resource(JsonElement item)
    {
        var signals = new SortedDictionary<string, string>(StringComparer.Ordinal);
        if (item.TryGetProperty("tags", out var tags) && tags.ValueKind != JsonValueKind.Null)
        {
            foreach (var tag in tags.EnumerateObject())
            {
                var key = tag.Name.ToLowerInvariant();
                if (key.StartsWith("dsf", StringComparison.Ordinal) || key is "environment" or "env" or "shared" or "dedicated"
                    or "managed-by" or "project" or "product" or "component")
                {
                    var value = tag.Value.GetString()?.Trim().ToLowerInvariant() ?? throw new FormatException();
                    signals.Add(key, key.StartsWith("dsf", StringComparison.Ordinal) ? "sha256:" + BoundaryPlanFingerprint.Hash(value) : value);
                }
            }
        }
        var properties = item.TryGetProperty("properties", out var p) ? p : default;
        var network = new SortedDictionary<string, string>(StringComparer.Ordinal);
        if (properties.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in properties.EnumerateObject().Where(p => p.Name.ToLowerInvariant() is
                         "publicnetworkaccess" or "publicnetworkaccessforquery" or "publicnetworkaccessforingestion"
                         or "networkacls" or "privateendpointconnections" or "features"))
                network.Add(property.Name.ToLowerInvariant(), BoundaryPlanFingerprint.Canonical(property.Value));
        }
        return new(Text(item, "id").TrimEnd('/').ToLowerInvariant(), Text(item, "type").ToLowerInvariant(),
            Text(item, "location").ToLowerInvariant(), signals,
            Optional(properties, "customerId"), Optional(properties, "WorkspaceResourceId")?.ToLowerInvariant(),
            (Optional(properties, "serverFarmId") ?? Optional(properties, "managedEnvironmentId"))?.ToLowerInvariant())
        {
            NetworkConfiguration = network,
        };
    }

    internal static string Text(JsonElement item, string name) =>
        item.GetProperty(name).GetString() is { Length: > 0 } value ? value : throw new FormatException();
    private static string? Optional(JsonElement item, string name) => item.ValueKind == JsonValueKind.Object
        && item.EnumerateObject().FirstOrDefault(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).Value is var value
        && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    private static string[] Strings(JsonElement item, string name) => item.GetProperty(name).EnumerateArray()
        .Select(s => s.GetString() ?? throw new FormatException()).Order(StringComparer.Ordinal).ToArray();
    private static string[]? OptionalStrings(JsonElement item, string name) =>
        item.TryGetProperty(name, out var value) && value.ValueKind != JsonValueKind.Null ? Strings(item, name) : null;
    internal static T Parse<T>(string operation, Func<T> parse)
    {
        try { return parse(); }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or FormatException
            or KeyNotFoundException or ArgumentException or IndexOutOfRangeException or OverflowException)
        {
            throw new BoundaryInspectionException(operation);
        }
    }
    internal static BoundaryPlanFinding Unavailable(string check, string detail) => new(check, BoundaryPlanState.Unknown,
        detail, "Azure / owner administrator", "Establish complete read access and reconcile current remote state; no repair or forced retry.");
}
