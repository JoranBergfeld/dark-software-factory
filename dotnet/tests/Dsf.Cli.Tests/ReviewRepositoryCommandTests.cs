using Dsf.Cli;
using Dsf.Core.Onboarding;
using Xunit;

namespace Dsf.Cli.Tests;

public sealed class ReviewRepositoryCommandTests
{
    [Fact]
    public async Task Missing_repo_option_fails_loudly_in_non_interactive_mode()
    {
        var terminal = new ScriptedTerminal(new TerminalCapabilities(false, false, false), []);
        var reviewer = new RecordingReviewer(EligibleReview());

        var exitCode = await CliApplication.InvokeAsync(
            ["onboard", "decide", "review-repository"], CancellationToken.None, terminal, reviewer);

        Assert.Equal(1, exitCode);
        Assert.Contains("--repo owner/name is required", terminal.Error, StringComparison.Ordinal);
        Assert.Null(reviewer.LastRequest);
    }

    [Fact]
    public async Task Invalid_repo_format_fails_loudly_without_calling_the_reviewer()
    {
        var terminal = new ScriptedTerminal(new TerminalCapabilities(false, false, false), []);
        var reviewer = new RecordingReviewer(EligibleReview());

        var exitCode = await CliApplication.InvokeAsync(
            ["onboard", "decide", "review-repository", "--repo", "not-a-full-name"],
            CancellationToken.None,
            terminal,
            reviewer);

        Assert.Equal(1, exitCode);
        Assert.Contains("must be 'owner/name'", terminal.Error, StringComparison.Ordinal);
        Assert.Null(reviewer.LastRequest);
    }

    [Fact]
    public async Task Eligible_repository_is_reported_with_exit_code_zero()
    {
        var terminal = new ScriptedTerminal(new TerminalCapabilities(false, false, false), []);
        var reviewer = new RecordingReviewer(EligibleReview());

        var exitCode = await CliApplication.InvokeAsync(
            ["onboard", "decide", "review-repository", "--repo", "acme/demo"],
            CancellationToken.None,
            terminal,
            reviewer);

        Assert.Equal(0, exitCode);
        Assert.Equal("acme", reviewer.LastRequest!.Owner);
        Assert.Equal("demo", reviewer.LastRequest!.Repository);
        Assert.Contains("eligibility: Eligible", terminal.Output, StringComparison.Ordinal);
        Assert.Contains("acme/demo", terminal.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Blocked_repository_is_reported_with_reasons_and_still_exits_zero()
    {
        var terminal = new ScriptedTerminal(new TerminalCapabilities(false, false, false), []);
        var review = new RepositoryAttachmentReview(
            new RepositoryIdentity("1", "acme", "demo", "main", Archived: true, IssuesEnabled: true, "private"),
            [],
            [],
            [],
            RepositoryAttachmentEligibility.Blocked,
            ["Repository 'acme/demo' is archived; onboarding does not unarchive it."]);
        var reviewer = new RecordingReviewer(review);

        var exitCode = await CliApplication.InvokeAsync(
            ["onboard", "decide", "review-repository", "--repo", "acme/demo"],
            CancellationToken.None,
            terminal,
            reviewer);

        Assert.Equal(0, exitCode);
        Assert.Contains("eligibility: Blocked", terminal.Output, StringComparison.Ordinal);
        Assert.Contains("blocking reasons:", terminal.Output, StringComparison.Ordinal);
        Assert.Contains("archived", terminal.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Reviewer_failure_is_reported_as_an_error_without_a_crash()
    {
        var terminal = new ScriptedTerminal(new TerminalCapabilities(false, false, false), []);
        var reviewer = new ThrowingReviewer(new InvalidOperationException("GH_TOKEN missing"));

        var exitCode = await CliApplication.InvokeAsync(
            ["onboard", "decide", "review-repository", "--repo", "acme/demo"],
            CancellationToken.None,
            terminal,
            reviewer);

        Assert.Equal(1, exitCode);
        Assert.Contains("GH_TOKEN missing", terminal.Error, StringComparison.Ordinal);
    }

    private static RepositoryAttachmentReview EligibleReview() => new(
        new RepositoryIdentity("1", "acme", "demo", "main", Archived: false, IssuesEnabled: true, "private"),
        [new RepositoryAttachmentPrerequisite("repository-access", PrerequisiteState.Confirmed, "readable", null)],
        [new RepositoryLabelReview("dsf:proposal", Exists: false, null, null, ConflictsWithExpectedMeaning: false)],
        [],
        RepositoryAttachmentEligibility.Eligible,
        []);

    private sealed class RecordingReviewer(RepositoryAttachmentReview review) : IRepositoryAttachmentReviewer
    {
        public RepositoryAttachmentReviewRequest? LastRequest { get; private set; }

        public Task<RepositoryAttachmentReview> ReviewAsync(
            RepositoryAttachmentReviewRequest request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            return Task.FromResult(review);
        }
    }

    private sealed class ThrowingReviewer(Exception exception) : IRepositoryAttachmentReviewer
    {
        public Task<RepositoryAttachmentReview> ReviewAsync(
            RepositoryAttachmentReviewRequest request, CancellationToken cancellationToken) =>
            throw exception;
    }
}
