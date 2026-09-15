using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Dsf.Core.Onboarding;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace Dsf.Cli;

internal sealed partial class RepositoryAttachmentReviewer
{
    private IReadOnlyList<RepositoryMutationReview> ReviewAutomation(
        RepositoryAttachmentReviewRequest request,
        ReviewedGitHubRepository repository,
        List<RepositoryReviewFinding> findings,
        CancellationToken cancellationToken)
    {
        var root = $"repos/{repository.Owner}/{repository.Name}";
        var evidence = Enum.GetValues<RepositoryMutationClass>().ToDictionary(mutation => mutation, _ => new List<string>());
        var blocked = new HashSet<RepositoryMutationClass>();
        var unknown = new HashSet<RepositoryMutationClass>();
        var workflowsComplete = InspectInventory(InspectWorkflows, "workflow");
        var hooksComplete = InspectInventory(InspectHooks, "webhook");
        var reviews = new List<RepositoryMutationReview>();
        foreach (var mutation in Enum.GetValues<RepositoryMutationClass>())
        {
            var complete = workflowsComplete && hooksComplete && !unknown.Contains(mutation);
            var declaration = request.AutomationDeclarations.GetValueOrDefault(mutation);
            var state = blocked.Contains(mutation) || declaration is RepositoryAutomationDeclaration.Conflict ? RepositoryReviewState.Blocked
                : !complete ? RepositoryReviewState.Unknown
                : declaration is RepositoryAutomationDeclaration.Unknown ? RepositoryReviewState.Pending
                : RepositoryReviewState.Confirmed;
            reviews.Add(new(mutation, state, declaration, request.AutomationOwner, complete, evidence[mutation]));
            findings.Add(new($"automation:{mutation}", state,
                $"External-owner declaration: {declaration}; visible inventory complete: {complete}. "
                + (blocked.Contains(mutation) ? "Known delivery/coding-agent trigger blocks this mutation. " : "")
                + string.Join(" ", evidence[mutation]) + " "
                + "Neither a declaration nor visible inventory proves the absence of every bot, webhook, reusable workflow or organization automation.",
                state is RepositoryReviewState.Confirmed ? null : request.AutomationOwner ?? "repository / external automation owner",
                state is RepositoryReviewState.Confirmed ? null
                    : $"Review {mutation} consumers and delivery/coding-agent triggers outside DSF. Supply a per-class declaration only after owner review; no override or automatic repair."));
        }

        return reviews;

        bool InspectInventory(Func<bool> inspect, string kind)
        {
            try
            {
                return inspect();
            }
            catch (Exception exception) when (InvalidData(exception))
            {
                AddAll($"GitHub returned incomplete {kind} metadata; administrator inspection is required.");
                return false;
            }
        }

        bool InspectWorkflows()
        {
            var inspected = 0;
            for (var page = 1; page <= 10; page++)
            {
                var response = ReadGitHub($"{root}/actions/workflows?per_page=100&page={page}", cancellationToken);
                if (response.Body is not { } body || !body.TryGetProperty("total_count", out var count)
                    || !count.TryGetInt32(out var total) || total < 0
                    || !body.TryGetProperty("workflows", out var items) || items.ValueKind != JsonValueKind.Array)
                {
                    AddAll($"Workflow inventory unavailable/incomplete (HTTP {response.Status}); repository Actions administrator must inspect.");
                    return false;
                }

                foreach (var workflow in items.EnumerateArray())
                {
                    inspected++;
                    var state = RequiredText(workflow, "state");
                    if (state is "deleted" or "disabled_fork" or "disabled_inactivity" or "disabled_manually")
                    {
                        continue;
                    }

                    if (state != "active")
                    {
                        AddAll("Workflow activation state is unknown; Actions owner must inspect it.");
                        unknown.UnionWith(Enum.GetValues<RepositoryMutationClass>());
                        continue;
                    }

                    var path = workflow.GetProperty("path").GetString();
                    if (path is null || !Regex.IsMatch(path, @"^\.github/workflows/[A-Za-z0-9_.-]+\.ya?ml$"))
                    {
                        AddAll("Active workflow has an unsupported path; Actions owner must inspect it.");
                        unknown.UnionWith(Enum.GetValues<RepositoryMutationClass>());
                        continue;
                    }

                    InspectWorkflow(path);
                }

                if (inspected == total)
                {
                    return true;
                }

                if (items.GetArrayLength() < 100 || inspected > total)
                {
                    AddAll("Workflow inventory changed or was truncated; rerun a stable review.");
                    return false;
                }
            }

            AddAll("Workflow inventory exceeds the 1000-workflow inspection limit; Actions owner must narrow the review externally.");
            return false;
        }

        void InspectWorkflow(string path)
        {
            var response = ReadGitHub(
                $"{root}/contents/{path}?ref={Uri.EscapeDataString(repository.DefaultBranch)}", cancellationToken);
            try
            {
                if (response.Body is not { } file || file.GetProperty("type").GetString() != "file"
                    || file.GetProperty("encoding").GetString() != "base64")
                {
                    throw new InvalidOperationException();
                }

                var content = RequiredText(file, "content");
                var sha = RequiredText(file, "sha");
                if (content.Length > 350_000)
                {
                    throw new InvalidOperationException();
                }

                var source = Encoding.UTF8.GetString(Convert.FromBase64String(content));
                var yaml = new YamlStream();
                yaml.Load(new StringReader(source));
                if (yaml.Documents.Count != 1 || yaml.Documents[0].RootNode is not YamlMappingNode mapping
                    || !mapping.Children.TryGetValue(new YamlScalarNode("on"), out var on))
                {
                    throw new InvalidOperationException();
                }

                var events = EventNames(on).ToArray();
                var affected = events.SelectMany(AffectedMutations).Distinct().ToArray();
                var known = mapping.Children.TryGetValue(new YamlScalarNode("jobs"), out var jobs)
                    && ScalarValues(jobs).Any(value => Regex.IsMatch(value,
                        @"@copilot|copilot-swe-agent|gh\s+agent-task|\b(deploy|deployment|publish)\b|kubectl\s+apply|terraform\s+apply|gh\s+(pr\s+merge|workflow\s+run)",
                        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant));
                foreach (var mutation in affected)
                {
                    evidence[mutation].Add($"Workflow {path}@{sha} subscribes to {string.Join(", ", events)}; "
                        + (known ? "delivery/coding-agent instruction observed." : "transitive/scripted effects cannot be established."));
                    (known ? blocked : unknown).Add(mutation);
                }
            }
            catch (Exception exception) when (exception is JsonException or InvalidOperationException
                or KeyNotFoundException or FormatException or YamlException or ArgumentException)
            {
                AddAll($"Workflow {path} could not be completely inspected (HTTP {response.Status}); source and provider errors withheld.");
                unknown.UnionWith(Enum.GetValues<RepositoryMutationClass>());
            }
        }

        bool InspectHooks()
        {
            for (var page = 1; page <= 10; page++)
            {
                var response = ReadGitHub($"{root}/hooks?per_page=100&page={page}", cancellationToken);
                if (response.Body is not { ValueKind: JsonValueKind.Array } items)
                {
                    AddAll($"Repository webhook inventory unavailable (HTTP {response.Status}); webhook/organization administrator must review without broader tokens.");
                    return false;
                }

                foreach (var hook in items.EnumerateArray())
                {
                    if (!hook.GetProperty("active").GetBoolean())
                    {
                        continue;
                    }

                    var events = hook.GetProperty("events").EnumerateArray().Select(value => value.GetString()!).ToArray();
                    foreach (var mutation in events.SelectMany(AffectedMutations).Distinct())
                    {
                        unknown.Add(mutation);
                        evidence[mutation].Add($"Active webhook {hook.GetProperty("id").GetInt64()} subscribes to {string.Join(", ", events)}; "
                            + "webhook owner must establish downstream effects. URL/configuration withheld.");
                    }
                }

                if (items.GetArrayLength() < 100)
                {
                    return true;
                }
            }

            AddAll("Webhook inventory exceeds the 1000-hook inspection limit; webhook owner must review.");
            return false;
        }

        void AddAll(string message)
        {
            foreach (var values in evidence.Values)
            {
                values.Add(message);
            }
        }
    }

    private static IEnumerable<string> EventNames(YamlNode node)
    {
        var nodes = node switch
        {
            YamlScalarNode => [node],
            YamlSequenceNode sequence => sequence.Children.AsEnumerable(),
            YamlMappingNode mapping => mapping.Children.Keys,
            _ => throw new InvalidOperationException("Unsupported workflow trigger syntax."),
        };
        return nodes.Select(item => item is YamlScalarNode { Value.Length: > 0 } scalar
            ? scalar.Value : throw new InvalidOperationException("Unsupported workflow event."));
    }

    private static IEnumerable<RepositoryMutationClass> AffectedMutations(string eventName) => eventName switch
    {
        "issues" => [RepositoryMutationClass.ProposalIssue],
        "issue_comment" => [RepositoryMutationClass.ProposalIssue, RepositoryMutationClass.CharterPullRequest],
        "pull_request" or "pull_request_target" or "pull_request_review" or "pull_request_review_comment" or "push" or "create"
            => [RepositoryMutationClass.CharterPullRequest],
        "label" => [RepositoryMutationClass.LabelCreation],
        "workflow_dispatch" or "repository_dispatch" or "schedule" or "workflow_call" => [],
        _ => Enum.GetValues<RepositoryMutationClass>(),
    };

    private static IEnumerable<string> ScalarValues(YamlNode root)
    {
        var pending = new Stack<YamlNode>();
        var visited = new HashSet<YamlNode>(ReferenceEqualityComparer.Instance);
        pending.Push(root);
        while (pending.TryPop(out var node))
        {
            if (!visited.Add(node))
            {
                continue;
            }

            switch (node)
            {
                case YamlScalarNode { Value: { } value }: yield return value; break;
                case YamlMappingNode mapping:
                    foreach (var value in mapping.Children.Values) { pending.Push(value); }
                    break;
                case YamlSequenceNode sequence:
                    foreach (var value in sequence.Children) { pending.Push(value); }
                    break;
            }
        }
    }
}
