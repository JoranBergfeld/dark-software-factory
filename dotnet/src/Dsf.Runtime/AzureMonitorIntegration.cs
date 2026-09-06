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
/// fallback). Queries the trailing 24 hours; a served agent runs on a sweep
/// cadence measured in minutes, so a wider window costs nothing but catches
/// evidence even after a gap in serving.
/// </summary>
internal sealed class AzureMonitorLogsGateway : IAzureMonitorLogsGateway
{
    private static readonly QueryTimeRange TimeRange = new(TimeSpan.FromHours(24));

    public async Task<IReadOnlyList<IReadOnlyDictionary<string, string>>> QueryAsync(
        string workspaceId, string query, CancellationToken cancellationToken)
    {
        var client = new LogsQueryClient(new DefaultAzureCredential());

        Azure.Response<LogsQueryResult> response;
        try
        {
            response = await client.QueryWorkspaceAsync(workspaceId, query, TimeRange, cancellationToken: cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw new InvalidOperationException(
                $"could not query Azure Monitor workspace '{workspaceId}': {exception.Message}", exception);
        }

        var table = response.Value.Table;
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
/// signals); this adapter only maps whatever columns it names for reference and
/// summary onto <see cref="EvidenceItem"/>, the same column-name-probing
/// approach the generic HTTP integration uses for JSON properties.
/// </summary>
internal sealed class AzureMonitorIntegration(IReadOnlyDictionary<string, string?> env, IAzureMonitorLogsGateway? gateway = null)
    : ISourceIntegration
{
    private static readonly string[] ReferenceColumns = ["Reference", "reference", "_ItemId", "Id", "id", "RowId"];
    private static readonly string[] SummaryColumns = ["Summary", "summary", "Message", "message", "Title", "title", "OperationName"];

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

        return rows
            .Select(row => new EvidenceItem("azuremonitor", First(row, ReferenceColumns), First(row, SummaryColumns)))
            .Where(item => item.Reference.Length > 0)
            .ToArray();
    }

    private static string First(IReadOnlyDictionary<string, string> row, IEnumerable<string> columns) =>
        columns
            .Select(column => row.TryGetValue(column, out var value) ? value : string.Empty)
            .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))
        ?? string.Empty;

    private string Read(string name) =>
        (env.TryGetValue(name, out var value) ? value : null)?.Trim() ?? string.Empty;
}
