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
                    : !complete ? $"Reconcile {mutation} inventory and downstream effects outside DSF, then rerun review. Repeating a declaration cannot resolve opaque effects; unresolved inspection remains Unknown. No override or automatic repair."
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

                var events = WorkflowEvents(on).ToArray();
                var known = HasKnownDeliveryStep(mapping);
                foreach (var subscription in events)
                {
                    foreach (var mutation in AffectedMutations(subscription.Name))
                    {
                        evidence[mutation].Add($"Workflow {path}@{sha} subscribes to {subscription.Name} (types: {subscription.Activities}); "
                            + (subscription.CausedByMutation is false ? "activity filter excludes this mutation."
                                : subscription.CausedByMutation is null ? "event/activity filter is unsupported or malformed; trigger relevance is unknown."
                                : known ? "delivery/coding-agent instruction observed." : "transitive/scripted effects cannot be established."));
                        if (subscription.CausedByMutation is not false)
                        {
                            (subscription.CausedByMutation is true && known ? blocked : unknown).Add(mutation);
                        }
                    }
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

    private sealed record WorkflowEvent(string Name, string Activities, bool? CausedByMutation);

    private static IEnumerable<WorkflowEvent> WorkflowEvents(YamlNode node)
    {
        if (node is YamlMappingNode mapping)
        {
            if (mapping.Children.Count == 0)
            {
                throw new InvalidOperationException("Empty workflow events.");
            }

            foreach (var (name, configuration) in mapping.Children)
            {
                yield return ReadWorkflowEvent(EventName(name), configuration);
            }
        }
        else if (node is YamlSequenceNode sequence)
        {
            if (sequence.Children.Count == 0)
            {
                throw new InvalidOperationException("Empty workflow events.");
            }

            foreach (var name in sequence.Children)
            {
                yield return ReadWorkflowEvent(EventName(name), null);
            }
        }
        else
        {
            yield return ReadWorkflowEvent(EventName(node), null);
        }

        static string EventName(YamlNode name) => name is YamlScalarNode { Value.Length: > 0 } scalar
            ? scalar.Value : throw new InvalidOperationException("Unsupported workflow event.");
    }

    private static WorkflowEvent ReadWorkflowEvent(string name, YamlNode? configuration)
    {
        (string[] Supported, string[] Caused)? activities = name switch
        {
            "issues" => (["opened", "edited", "deleted", "transferred", "pinned", "unpinned", "closed", "reopened",
                "assigned", "unassigned", "labeled", "unlabeled", "locked", "unlocked", "milestoned", "demilestoned",
                "typed", "untyped", "field_added", "field_removed"], ["opened", "labeled"]),
            "pull_request" or "pull_request_target" => (["assigned", "unassigned", "labeled", "unlabeled", "opened",
                "edited", "closed", "reopened", "synchronize", "converted_to_draft", "locked", "unlocked", "enqueued",
                "dequeued", "milestoned", "demilestoned", "ready_for_review", "review_requested", "review_request_removed",
                "auto_merge_enabled", "auto_merge_disabled"], ["opened"]),
            "label" => (["created", "edited", "deleted"], ["created"]),
            "issue_comment" or "pull_request_review_comment" => (["created", "edited", "deleted"], []),
            "pull_request_review" => (["submitted", "edited", "dismissed"], []),
            _ => null,
        };
        if (activities is null && name is not ("push" or "create" or "workflow_dispatch" or "repository_dispatch" or "schedule" or "workflow_call"))
        {
            return new(name, "unknown", null);
        }

        var defaultRelevance = activities is not { Caused.Length: 0 };
        if (configuration is null || configuration is YamlScalarNode
            {
                Style: ScalarStyle.Plain,
                Value: null or "" or "~" or "null" or "Null" or "NULL",
            })
        {
            return new(name, "default", defaultRelevance);
        }

        if (configuration is not YamlMappingNode options)
        {
            return new(name, "unknown", null);
        }

        if (!options.Children.TryGetValue(new YamlScalarNode("types"), out var types))
        {
            return new(name, "default", options.Children.Count == 0 ? defaultRelevance : null);
        }

        if (activities is not { } contract || types is not YamlSequenceNode { Children.Count: > 0 } sequence
            || sequence.Children.Any(item => item is not YamlScalarNode { Value: { } value } || !contract.Supported.Contains(value)))
        {
            return new(name, "unknown", null);
        }

        var selected = sequence.Children.Cast<YamlScalarNode>().Select(item => item.Value!).ToArray();
        return new(name, string.Join(", ", selected),
            options.Children.Count != 1 ? null : selected.Intersect(contract.Caused).Any());
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

    private static bool HasKnownDeliveryStep(YamlMappingNode workflow)
    {
        if (!workflow.Children.TryGetValue(new YamlScalarNode("jobs"), out var jobs) || jobs is not YamlMappingNode jobMapping)
        {
            return false;
        }

        foreach (var job in jobMapping.Children.Values.OfType<YamlMappingNode>())
        {
            if (!job.Children.TryGetValue(new YamlScalarNode("steps"), out var steps) || steps is not YamlSequenceNode sequence)
            {
                continue;
            }

            foreach (var step in sequence.Children.OfType<YamlMappingNode>())
            {
                if (step.Children.TryGetValue(new YamlScalarNode("run"), out var run)
                    && run is YamlScalarNode { Value: { } command }
                    && HasStandardShell(step, job, workflow) && IsKnownDeliveryCommand(command))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool HasStandardShell(YamlMappingNode step, YamlMappingNode job, YamlMappingNode workflow)
    {
        if (step.Children.TryGetValue(new YamlScalarNode("shell"), out var shell))
        {
            return Standard(shell);
        }

        foreach (var scope in new[] { job, workflow })
        {
            if (!scope.Children.TryGetValue(new YamlScalarNode("defaults"), out var defaults))
            {
                continue;
            }

            if (defaults is not YamlMappingNode mapping) { return false; }
            if (!mapping.Children.TryGetValue(new YamlScalarNode("run"), out var run)) { continue; }
            if (run is not YamlMappingNode settings) { return false; }
            if (settings.Children.TryGetValue(new YamlScalarNode("shell"), out shell)) { return Standard(shell); }
        }

        return true;

        static bool Standard(YamlNode shell) => shell is YamlScalarNode { Value: "bash" or "sh" or "pwsh" or "powershell" };
    }

    private static bool IsKnownDeliveryCommand(string source)
    {
        // Only recognize a simple command, not prose, arguments to echo, or opaque shell programs.
        var command = source.Trim();
        if (command.IndexOfAny(['\r', '\n', ';', '|', '&', '<', '>', '`', '\\']) >= 0 || command.Contains("$(", StringComparison.Ordinal))
        {
            return false;
        }

        var tokens = new List<string>();
        var end = 0;
        foreach (Match match in Regex.Matches(command, """\G[ \t]*(?:'([^']*)'|"([^"]*)"|([^\s'"]+))(?=[ \t]|\z)"""))
        {
            tokens.Add(match.Groups.Cast<Group>().Skip(1).First(group => group.Success).Value);
            end = match.Index + match.Length;
        }

        if (end != command.Length || tokens.Any(token => token is "--help" or "-h" or "--dry-run"
            || token.StartsWith("--dry-run=", StringComparison.Ordinal)))
        {
            return false;
        }

        var arguments = tokens.ToArray();
        return arguments is ["gh", "agent-task", "create", ..] or ["gh", "pr", "merge", ..]
            or ["npm", "publish", ..] or ["kubectl", "apply", ..] or ["terraform", "apply", ..]
            || IsCopilotAssignment(arguments);
    }

    private static bool IsCopilotAssignment(string[] arguments)
    {
        if (arguments is not ["gh", "issue", "edit", _, ..]) { return false; }
        var assignment = false;
        for (var index = 4; index < arguments.Length; index++)
        {
            var option = arguments[index].Split('=', 2);
            if (option[0] is not ("--add-assignee" or "--body" or "--title" or "--repo" or "-R")) { return false; }
            var value = option.Length == 2 ? option[1] : ++index < arguments.Length ? arguments[index] : null;
            if (value is null) { return false; }
            assignment |= option[0] == "--add-assignee" && value == "@copilot";
        }

        return assignment;
    }
}
