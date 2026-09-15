using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Dsf.Core.Onboarding;

namespace Dsf.Cli;

internal sealed record RepositoryAttachmentReviewRequest(
    string Repository,
    string? OwnerAppConfigEndpoint,
    string? OwnerKeyVaultUri)
{
    public string? AutomationOwner { get; init; }
    public IReadOnlyDictionary<RepositoryMutationClass, RepositoryAutomationDeclaration> AutomationDeclarations { get; init; } =
        new Dictionary<RepositoryMutationClass, RepositoryAutomationDeclaration>();

    public void Validate()
    {
        ValidateEndpoint(OwnerAppConfigEndpoint, "--owner-appconfig-endpoint", ".azconfig.io");
        ValidateEndpoint(OwnerKeyVaultUri, "--owner-keyvault-uri", ".vault.azure.net");
    }

    private static void ValidateEndpoint(string? value, string option, string suffix)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps
            || !uri.Host.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) || uri.UserInfo.Length != 0
            || uri.Query.Length != 0 || uri.Fragment.Length != 0 || uri.AbsolutePath != "/" || !uri.IsDefaultPort)
        {
            throw new InvalidOperationException($"{option} must be an HTTPS {suffix} endpoint without credentials, path, query or fragment.");
        }
    }
}

internal sealed partial class RepositoryAttachmentReviewer(IGhCliRunner gh, IOwnerCredentialReader owner, HttpClient appHttp)
{
    private static readonly HttpClient AppHttp = new(new HttpClientHandler { AllowAutoRedirect = false })
    {
        BaseAddress = new Uri("https://api.github.com/"),
    };

    public static RepositoryAttachmentReviewer Create() => new(
        new SystemGhCliRunner(), new AzureCliOwnerBootstrapClient(new SystemAzureCliRunner()), AppHttp);

    public async Task<RepositoryAttachmentReview> ReviewAsync(RepositoryAttachmentReviewRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        request.Validate();
        var identity = ReadGitHub("user", cancellationToken);
        if (identity.Body is not { } identityBody)
        {
            return new RepositoryAttachmentReview(1, DateTimeOffset.UtcNow, null,
            [
                new("operator-identity", identity.State, $"Effective GitHub CLI identity could not be established (HTTP {identity.Status}).",
                    "operator", "Check gh auth status for github.com; do not request broader credentials."),
            ]);
        }

        RepositoryReviewOperator actor;
        try
        {
            actor = new RepositoryReviewOperator(
                identityBody.GetProperty("id").GetInt64(), RequiredText(identityBody, "login"));
        }
        catch (Exception exception) when (InvalidData(exception))
        {
            return new(1, DateTimeOffset.UtcNow, null,
                [Incomplete("operator-identity", "GitHub returned incomplete operator identity metadata.")]);
        }
        var result = ReadGitHub($"repos/{request.Repository}", cancellationToken);
        if (result.Body is not { } value)
        {
            return new RepositoryAttachmentReview(1, DateTimeOffset.UtcNow, null,
            [
                new("repository-access", result.State,
                    $"Repository could not be inspected with effective GitHub CLI authentication (HTTP {result.Status}).",
                    "repository administrator / operator",
                    "Confirm repository visibility and organization SSO/policy eligibility using the existing gh identity; do not broaden the token."),
            ]) { Operator = actor };
        }

        ReviewedGitHubRepository observed;
        try
        {
            observed = ReadRepository(value);
        }
        catch (Exception exception) when (InvalidData(exception))
        {
            return new RepositoryAttachmentReview(1, DateTimeOffset.UtcNow, null,
                [Incomplete("repository-metadata", "GitHub returned incomplete repository identity/settings; no default branch was inferred.")]) { Operator = actor };
        }
        var compatible = !observed.Archived && !observed.Disabled && observed.HasIssues;
        var access = await new OwnerRepositoryAccessReviewer(owner, appHttp).ReviewAsync(request, observed, cancellationToken);
        var findings = new List<RepositoryReviewFinding>();
        var labels = ReviewLabels(observed, findings, cancellationToken);
        var automation = ReviewAutomation(request, observed, findings, cancellationToken);
        var current = ReadGitHub($"repos/{observed.Owner}/{observed.Name}", cancellationToken);
        try
        {
            if (current.Body is not { } currentBody)
            {
                findings.Add(Incomplete("repository-stability", $"Final repository inspection unavailable (HTTP {current.Status}); earlier facts are not current authorization."));
            }
            else if (ReadRepository(currentBody) != observed)
            {
                findings.Add(new("repository-stability", RepositoryReviewState.Blocked,
                    "Repository identity, name, default branch or settings changed during review; assessment was not retargeted.",
                    "repository administrator / operator", "Rerun read-only review against the intended durable repository identity."));
            }
            else
            {
                findings.Add(new("repository-stability", RepositoryReviewState.Confirmed,
                    "Repository identity and metadata were stable across this inspection; later operations must revalidate all facts."));
            }
        }
        catch (Exception exception) when (InvalidData(exception))
        {
            findings.Add(Incomplete("repository-stability", "Final repository metadata is incomplete; earlier facts are not current authorization."));
        }

        return new RepositoryAttachmentReview(1, DateTimeOffset.UtcNow, observed,
        [
            new("repository-access", RepositoryReviewState.Confirmed, "Readable using effective GitHub CLI authentication; visibility is not installation consent."),
            new("repository-settings", compatible ? RepositoryReviewState.Confirmed : RepositoryReviewState.Blocked,
                $"Archived={observed.Archived}; disabled={observed.Disabled}; issues enabled={observed.HasIssues}. No repository settings were changed.",
                compatible ? null : "repository administrator",
                compatible ? null : "This repository is incompatible with attachment. Resolve repository policy outside DSF; no replacement or automatic repair is offered."),
            .. access.Findings,
            .. findings,
        ]) { Operator = actor, App = access.App, Installation = access.Installation, Labels = labels, Automation = automation };
    }

    private IReadOnlyList<ReviewedRepositoryLabel> ReviewLabels(
        ReviewedGitHubRepository repository, List<RepositoryReviewFinding> findings, CancellationToken cancellationToken)
    {
        var labels = new List<ReviewedRepositoryLabel>();
        foreach (var (name, meaning) in DecideRepositoryLabels.Meanings)
        {
            var response = ReadGitHub($"repos/{repository.Owner}/{repository.Name}/labels/{Uri.EscapeDataString(name)}", cancellationToken);
            if (response.Status == 404)
            {
                labels.Add(new(name, null, null, null, meaning, RepositoryReviewState.Confirmed));
                findings.Add(new($"label:{name}", RepositoryReviewState.Confirmed,
                    $"Label is absent; required meaning: {meaning} No label was created."));
                continue;
            }

            if (response.Body is not { } label)
            {
                labels.Add(new(name, null, null, null, meaning, response.State));
                findings.Add(new($"label:{name}", response.State, $"Label inspection unavailable (HTTP {response.Status}).",
                    "repository administrator", "Review label visibility with existing gh authorization; no label changes were made."));
                continue;
            }

            try
            {
                var observedName = RequiredText(label, "name");
                var description = label.GetProperty("description").GetString();
                var matches = string.Equals(NormalizeLabelDescription(description), NormalizeLabelDescription(meaning),
                    StringComparison.OrdinalIgnoreCase);
                var state = matches ? RepositoryReviewState.Confirmed
                    : HasDeliveryAssignmentMeaning(description) ? RepositoryReviewState.Blocked : RepositoryReviewState.Unknown;
                if (!string.Equals(name, observedName, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException();
                }

                labels.Add(new(observedName, label.GetProperty("id").GetInt64(), RequiredText(label, "color"), description, meaning, state));
                findings.Add(new($"label:{name}", state,
                    $"Required meaning: {meaning} Observed description: {description ?? "(none)"}. "
                    + (matches ? "Existing identity/color/description remain unchanged."
                        : state is RepositoryReviewState.Blocked ? "Explicit delivery/assignment meaning conflicts with proposal-only use; no label changes or reuse are authorized."
                        : "Existing meaning is unresolved; no label changes or reuse are authorized."),
                    matches ? null : "repository label / automation owner",
                    matches ? null : "Reconcile this label's meaning and consumers outside DSF; review cannot override a semantic conflict."));
            }
            catch (Exception exception) when (InvalidData(exception))
            {
                labels.Add(new(name, null, null, null, meaning, RepositoryReviewState.Unknown));
                findings.Add(Incomplete($"label:{name}", "Label metadata is incomplete; it cannot be treated as absent or compatible."));
            }
        }

        return labels;
    }

    private static string NormalizeLabelDescription(string? description) =>
        Regex.Replace(description ?? "", @"[\s.,;:\-]+", " ").Trim();

    private static bool HasDeliveryAssignmentMeaning(string? description) =>
        Regex.Split(description ?? "", @"[.;:\r\n]+").Any(clause =>
        {
            var text = NormalizeLabelDescription(clause);
            const RegexOptions options = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;
            return !Regex.IsMatch(text, @"\b(not|never|without|don['\u2019]t)\b", options)
                && Regex.IsMatch(text,
                    @"^(?:(?:automatically )?(?:assign\b.*(?:coding agent|@copilot)\b|deploy\b)"
                    + @"|(?:a )?delivery assignment\b|(?:accepted|approved|ready) for (?:delivery|deployment)\b)",
                    options);
        });

    private GitHubRead ReadGitHub(string path, CancellationToken cancellationToken)
    {
        GhInvocationResult result;
        try
        {
            result = gh.Run(
                ["api", "--hostname", "github.com", "--method", "GET", "--include",
                    "--header", "X-GitHub-Api-Version: 2022-11-28", path], cancellationToken);
        }
        catch (GhUnavailableException)
        {
            return new(0, null);
        }

        var text = result.StandardOutput.Replace("\r\n", "\n", StringComparison.Ordinal);
        var status = 0;
        while (text.StartsWith("HTTP/", StringComparison.Ordinal))
        {
            var end = text.IndexOf("\n\n", StringComparison.Ordinal);
            var firstLine = text.Split('\n', 2)[0].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (end < 0 || firstLine.Length < 2 || !int.TryParse(firstLine[1], CultureInfo.InvariantCulture, out status))
            {
                return new(0, null);
            }

            text = text[(end + 2)..];
        }

        if (result.ExitCode != 0 || status is < 200 or >= 300)
        {
            return new(status, null);
        }

        try
        {
            using var document = JsonDocument.Parse(text);
            return new(status, document.RootElement.Clone());
        }
        catch (JsonException)
        {
            return new(status, null);
        }
    }

    private sealed record GitHubRead(int Status, JsonElement? Body)
    {
        public RepositoryReviewState State => Status is 401 or 403
            ? RepositoryReviewState.Denied : RepositoryReviewState.Unknown;
    }

    private static RepositoryReviewFinding Incomplete(string check, string detail) =>
        new(check, RepositoryReviewState.Unknown, detail, "repository administrator / operator",
            "Re-establish a complete read-only GitHub inspection with existing authorization; provider payloads are withheld.");

    private static bool InvalidData(Exception exception) =>
        exception is JsonException or KeyNotFoundException or InvalidOperationException or FormatException or OverflowException;

    private static string RequiredText(JsonElement value, string property) =>
        value.GetProperty(property).GetString() is { Length: > 0 } text && !string.IsNullOrWhiteSpace(text)
            ? text : throw new InvalidOperationException("Required metadata is missing.");

    private static ReviewedGitHubRepository ReadRepository(JsonElement value)
    {
        var repositoryOwner = value.GetProperty("owner");
        var repository = new ReviewedGitHubRepository(
            value.GetProperty("id").GetInt64(), repositoryOwner.GetProperty("id").GetInt64(),
            RequiredText(repositoryOwner, "login"), RequiredText(value, "name"),
            RequiredText(value, "default_branch"), RequiredText(value, "visibility"),
            value.GetProperty("archived").GetBoolean(), value.GetProperty("disabled").GetBoolean(),
            value.GetProperty("has_issues").GetBoolean());
        return repository.Id > 0 && repository.OwnerId > 0 ? repository : throw new InvalidOperationException("Invalid repository identity.");
    }
}
