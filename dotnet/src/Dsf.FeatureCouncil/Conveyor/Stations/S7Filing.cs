using Dsf.Core.Runtime;

namespace Dsf.FeatureCouncil.Conveyor.Stations;

/// <summary>
/// S7 — filing. On a dry run the line stops here deliberately (status
/// <see cref="RunStatus.Previewed"/>) with nothing filed. Otherwise every accepted,
/// routed proposal is handed to the issue filer. When there is something to file
/// and no filer is wired, this is the real boundary the run fails at -- after
/// stations S1..S6 have already done and checkpointed their work. A run that files
/// nothing because it accepted nothing is reported as such, never as a filing that
/// silently had no filer behind it.
/// </summary>
public sealed class S7Filing : IStation
{
    public const string StationName = "s7_filing";

    public string Name => StationName;

    public async Task RunAsync(ConveyorRun run, ConveyorServices services, CancellationToken cancellationToken)
    {
        var accepted = run.Proposals.Where(proposal => proposal.Accepted).ToList();
        if (run.DryRun)
        {
            run.Status = RunStatus.Previewed;
            run.Record(StationName, $"dry run: skipped filing {accepted.Count} accepted proposal(s).");
            return;
        }

        if (accepted.Count > 0 && services.IssueFiler is null)
        {
            throw new InvalidOperationException(
                $"cannot file {accepted.Count} accepted proposal(s) for product '{services.Product}': no GitHub "
                + "issue filer is wired (set GITHUB_APP_ID, GITHUB_INSTALLATION_ID, "
                + "GITHUB_APP_PRIVATE_KEY_SECRET, AZURE_KEYVAULT_URI, and "
                + $"{RuntimeIntegrationSettings.GitHubRepository}). Re-run with --dry-run to preview the line "
                + "without filing.");
        }

        foreach (var proposal in accepted)
        {
            var url = await services.IssueFiler!.FileAsync(proposal, cancellationToken);
            run.FiledIssues.Add(url);
            run.Record(StationName, $"filed proposal '{proposal.Id}' -> {url}");
        }

        run.Status = RunStatus.Filed;
        run.Record(StationName, $"filing complete: {run.FiledIssues.Count} issue(s) filed.");
    }
}
