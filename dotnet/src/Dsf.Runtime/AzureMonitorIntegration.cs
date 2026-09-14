using Azure.Identity;
using Azure.Monitor.Query;
using Azure.Monitor.Query.Models;
using Dsf.Core.Runtime;
using Dsf.FeatureCouncil.Conveyor;

namespace Dsf.Runtime;

/// <summary>
/// Runs one KQL query against a Log Analytics workspace and hands back its rows
/// as plain string columns, so <see cref="AzureMonitorIntegration"/> can map them
/// onto evidence without depending on the Azure SDK's row/column types directly.
/// The real implementation (<see cref="AzureMonitorLogsGateway"/>) wraps
/// <c>Azure.Monitor.Query.LogsQueryClient</c>; tests substitute a scripted
/// double instead of a live workspace.
/// </summary>
internal interface IAzureMonitorLogsGateway
{
    Task<IReadOnlyList<IReadOnlyDictionary<string, string>>> QueryAsync(
        string workspaceId, string query, CancellationToken cancellationToken);
}

/// <summary>
/// Wraps <c>Azure.Monitor.Query.LogsQueryClient</c> authenticated via <see
/// cref="DefaultAzureCredential"/> -- the same managed-identity-capable pattern
/// every other real Azure adapter in this project uses (ADR 0014: no offline
/// fallback). Queries the trailing 24 hours and rejects partial/truncated results;
/// the Logs query API has no continuation token.
/// </summary>
internal sealed class AzureMonitorLogsGateway(LogsQueryClient? client = null) : IAzureMonitorLogsGateway
{
    private static readonly QueryTimeRange TimeRange = new(TimeSpan.FromHours(24));
    private readonly LogsQueryClient client = client ?? new LogsQueryClient(new DefaultAzureCredential());

    public async Task<IReadOnlyList<IReadOnlyDictionary<string, string>>> QueryAsync(
        string workspaceId, string query, CancellationToken cancellationToken)
    {
        Azure.Response<LogsQueryResult> response;
        try
        {
            response = await client.QueryWorkspaceAsync(workspaceId, query, TimeRange,
                new LogsQueryOptions { AllowPartialErrors = false }, cancellationToken);
        }
        catch (Azure.RequestFailedException exception)
        {
            throw new InvalidOperationException(
                $"could not query Azure Monitor workspace '{workspaceId}': {exception.Message}", exception);
        }

        if (response.Value.AllTables.Count != 1)
        {
            throw new InvalidOperationException(
                $"{RuntimeIntegrationSettings.AzureMonitorQuery} must return one table with string Reference and Summary columns.");
        }

        var table = response.Value.Table;
        foreach (var required in new[] { "Reference", "Summary" })
        {
            if (!table.Columns.Any(column => column.Name.Equals(required, StringComparison.OrdinalIgnoreCase)
                && column.Type == LogsColumnType.String))
            {
                throw new InvalidOperationException(
                    $"{RuntimeIntegrationSettings.AzureMonitorQuery} must project string Reference and Summary columns.");
            }
        }

        var rows = new List<IReadOnlyDictionary<string, string>>(table.Rows.Count);
        foreach (var row in table.Rows)
        {
            var record = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (var column = 0; column < table.Columns.Count; column++)
            {
                record[table.Columns[column].Name] = row[column]?.ToString() ?? string.Empty;
            }

            rows.Add(record);
        }

        return rows;
    }
}

/// <summary>
/// The typed <c>azuremonitor</c> source integration: reads evidence from a real
/// Azure Monitor Log Analytics workspace via a KQL query, rather than the
/// generic JSON-shape-guessing <see cref="HttpSourceIntegration"/> fallback. The
/// operator's query decides what counts as evidence (which tables, which
/// signals) and must project string <c>Reference</c> and <c>Summary</c> columns.
/// Invalid rows fail the entire gather rather than silently discarding evidence.
/// </summary>
internal sealed class AzureMonitorIntegration(IReadOnlyDictionary<string, string?> env, IAzureMonitorLogsGateway? gateway = null)
    : ISourceIntegration
{
    private readonly IAzureMonitorLogsGateway gateway = gateway ?? new AzureMonitorLogsGateway();

    public async Task<IReadOnlyList<EvidenceItem>> GatherAsync(
        string kind, string product, CancellationToken cancellationToken)
    {
        var workspaceId = Read(RuntimeIntegrationSettings.AzureMonitorWorkspaceId);
        if (workspaceId.Length == 0)
        {
            throw new RuntimeConfigurationException(
                $"the 'azuremonitor' source agent for product '{product}' has no Log Analytics workspace "
                + $"configured: set {RuntimeIntegrationSettings.AzureMonitorWorkspaceId} to the workspace "
                + "ID it reads evidence from.",
                [RuntimeIntegrationSettings.AzureMonitorWorkspaceId]);
        }

        var query = Read(RuntimeIntegrationSettings.AzureMonitorQuery);
        if (query.Length == 0)
        {
            throw new RuntimeConfigurationException(
                $"the 'azuremonitor' source agent for product '{product}' has no KQL query configured: "
                + $"set {RuntimeIntegrationSettings.AzureMonitorQuery} to the query it runs against "
                + $"workspace '{workspaceId}'.",
                [RuntimeIntegrationSettings.AzureMonitorQuery]);
        }

        var rows = await gateway.QueryAsync(workspaceId, query, cancellationToken);

        return rows.Select(row =>
        {
            if (!row.TryGetValue("Reference", out var reference) || string.IsNullOrWhiteSpace(reference)
                || !row.TryGetValue("Summary", out var summary) || string.IsNullOrWhiteSpace(summary))
            {
                throw new InvalidOperationException(
                    $"{RuntimeIntegrationSettings.AzureMonitorQuery} returned a row without a nonempty Reference or Summary.");
            }

            return new EvidenceItem("azuremonitor", reference, summary);
        }).ToArray();
    }

    private string Read(string name) =>
        (env.TryGetValue(name, out var value) ? value : null)?.Trim() ?? string.Empty;
}
