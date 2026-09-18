using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Dsf.Core.Onboarding;
using Dsf.Runtime.GitHubApp;
using Xunit;

namespace Dsf.Runtime.Tests;

public sealed class FactoryGitHubPublicationTests
{
    [Theory]
    [InlineData("push")]
    [InlineData("pull_request")]
    [InlineData("check_run")]
    [InlineData("check_suite")]
    public async Task Current_unrelated_workflow_and_webhook_do_not_block_proposal_mutation(string subscription)
    {
        using var native = new PublicationTransport();
        using var github = new AuthorityTransport { PublicationEnabled = true, WorkflowEvent = subscription, HookEvent = subscription };
        using var remote = new PublicationWorkflowTransport(native, github);
        using var http = new HttpClient(remote);

        var result = await Publisher(http).CreateAsync(FactoryPublicationJournalTests.Intent(), Authority(github), CancellationToken.None);

        Assert.Equal(FactoryPublicationState.Confirmed, result.State);
        Assert.Single(github.IssueCreates);
        Assert.Contains(github.Paths, p => p.EndsWith("/contents/.github/workflows/existing.yml", StringComparison.Ordinal));
        Assert.NotNull(result.Attempts.Single().MutationReviewSha256);
    }

    [Theory]
    [InlineData("issues")]
    [InlineData("repository_dispatch")]
    [InlineData("unknown_event")]
    public async Task Applicable_or_opaque_workflow_is_not_cleared_by_an_owner_claim(string subscription)
    {
        using var native = new PublicationTransport();
        using var github = new AuthorityTransport { PublicationEnabled = true, WorkflowEvent = subscription };
        using var remote = new PublicationWorkflowTransport(native, github);
        using var http = new HttpClient(remote);
        await Assert.ThrowsAnyAsync<InvalidOperationException>(() =>
            Publisher(http).CreateAsync(FactoryPublicationJournalTests.Intent(), Authority(github), CancellationToken.None));
        Assert.Empty(github.IssueCreates);
    }

    [Theory]
    [InlineData("receipt-id")]
    [InlineData("receipt-request")]
    [InlineData("response")]
    [InlineData("origin-version")]
    public async Task Recomputed_document_checksum_cannot_launder_changed_original_creation_evidence(string damage)
    {
        using var native = new PublicationTransport();
        using var github = new AuthorityTransport { PublicationEnabled = true };
        using var remote = new PublicationWorkflowTransport(native, github);
        using var http = new HttpClient(remote);
        var intent = FactoryPublicationJournalTests.Intent();
        Assert.Equal(FactoryPublicationState.Confirmed, (await Publisher(http).CreateAsync(intent, Authority(github), CancellationToken.None)).State);
        var id = FactoryPublication.Id(intent);
        var changed = JsonNode.Parse(native.Documents[id])!.AsObject();
        if (damage == "receipt-id") changed["receipt"]!["issueId"] = 1;
        if (damage == "receipt-request") changed["receipt"]!["requestId"] = "copied-receipt";
        if (damage == "response") changed["receipt"]!["responseJson"] = "{}";
        if (damage == "origin-version") changed["origin"]!["nativeRevision"] = "\"unconfirmed-version\"";
        var payload = changed.DeepClone().AsObject();
        foreach (var key in AzureCosmosDocumentGateway.SystemProperties) payload.Remove(key);
        changed["checksum"] = payload.Deserialize<FactoryPublicationDocument>(FactorySourceValidation.RecordJson)!.CalculateChecksum();
        native.Documents[id] = changed.ToJsonString();

        await Assert.ThrowsAsync<JsonException>(() => Publisher(http).CreateAsync(intent, Authority(github), CancellationToken.None));

        Assert.Single(github.IssueCreates);
    }

    [Fact]
    public async Task Two_serialized_publishers_contend_on_native_dispatch_before_exactly_one_GitHub_create()
    {
        using var native = new PublicationTransport();
        using var github = new AuthorityTransport { PublicationEnabled = true };
        using var remote = new PublicationWorkflowTransport(native, github);
        using var http = new HttpClient(remote);
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var released = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var claims = 0;
        native.BeforeWrite = async (request, token) =>
        {
            var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(token))!;
            if ((string?)body["state"] == "Dispatched" && Interlocked.Increment(ref claims) == 1)
            {
                reached.SetResult();
                await released.Task.WaitAsync(token);
            }
        };
        var first = Publisher(http).CreateAsync(FactoryPublicationJournalTests.Intent(), Authority(github), CancellationToken.None);
        await reached.Task.WaitAsync(TimeSpan.FromSeconds(10));
        FactoryPublicationResult second;
        try { second = await Publisher(http).CreateAsync(FactoryPublicationJournalTests.Intent(), Authority(github), CancellationToken.None); }
        finally { released.SetResult(); }
        await Assert.ThrowsAsync<CosmosCheckpointException>(() => first);

        Assert.Equal(FactoryPublicationState.Confirmed, second.State);
        Assert.Single(github.IssueCreates);
        Assert.Single(second.Attempts);
    }

    [Theory]
    [InlineData("Dispatched", false)]
    [InlineData("Confirmed", true)]
    public async Task Lost_native_checkpoint_response_never_reissues_create_and_original_receipt_can_be_read_back(string phase, bool created)
    {
        using var native = new PublicationTransport();
        using var github = new AuthorityTransport { PublicationEnabled = true };
        using var remote = new PublicationWorkflowTransport(native, github);
        using var http = new HttpClient(remote);
        native.BeforeWrite = async (request, token) =>
        {
            if ((string?)JsonNode.Parse(await request.Content!.ReadAsStringAsync(token))!["state"] == phase)
            {
                native.BeforeWrite = null;
                native.LoseWriteResponse = true;
            }
        };
        var intent = FactoryPublicationJournalTests.Intent();
        if (!created)
            await Assert.ThrowsAsync<HttpRequestException>(() => Publisher(http).CreateAsync(intent, Authority(github), CancellationToken.None));
        else
            Assert.Equal(FactoryPublicationState.Unknown, (await Publisher(http).CreateAsync(intent, Authority(github), CancellationToken.None)).State);

        var restarted = await Publisher(http).CreateAsync(intent, Authority(github), CancellationToken.None);

        Assert.Equal(created ? FactoryPublicationState.Confirmed : FactoryPublicationState.Unknown, restarted.State);
        Assert.Equal(created ? 1 : 0, github.IssueCreates.Count);
        Assert.Equal(created, restarted.Receipt is not null);
    }

    [Fact]
    public async Task Mutation_inventory_change_before_issue_dispatch_retains_known_no_effect_and_can_retry_safely()
    {
        using var native = new PublicationTransport();
        using var github = new AuthorityTransport { PublicationEnabled = true };
        using var remote = new PublicationWorkflowTransport(native, github);
        using var http = new HttpClient(remote);
        native.BeforeWrite = async (request, token) =>
        {
            if ((string?)JsonNode.Parse(await request.Content!.ReadAsStringAsync(token))!["state"] == "Dispatched")
                github.PublicationConflict = "hook";
        };

        var result = await Publisher(http).CreateAsync(FactoryPublicationJournalTests.Intent(), Authority(github), CancellationToken.None);

        Assert.Equal("NotDispatched", result.State.ToString());
        Assert.Empty(github.IssueCreates);
        Assert.NotNull(result.Attempts.Single().MutationReviewSha256);
        native.BeforeWrite = null;
        github.PublicationConflict = null;
        var retry = await Publisher(http).CreateAsync(FactoryPublicationJournalTests.Intent(), Authority(github), CancellationToken.None);
        Assert.Equal(FactoryPublicationState.Confirmed, retry.State);
        Assert.Single(github.IssueCreates);
        Assert.Equal(2, retry.Attempts.Count);
    }

    [Fact]
    public async Task Original_native_creation_receipt_is_retained_without_exposing_issue_token_or_editing_issue()
    {
        using var native = new PublicationTransport();
        using var github = new AuthorityTransport { PublicationEnabled = true };
        using var remote = new PublicationWorkflowTransport(native, github);
        using var http = new HttpClient(remote);
        var intent = FactoryPublicationJournalTests.Intent();
        github.BeforeIssueCreate = () =>
        {
            var persisted = JsonNode.Parse(native.Documents[FactoryPublication.Id(intent)])!;
            Assert.Equal("Dispatched", (string?)persisted["state"]);
            Assert.Equal(intent.Title, (string?)persisted["intent"]!["title"]);
            Assert.Equal(intent.Body, (string?)persisted["intent"]!["body"]);
        };

        var result = await Publisher(http).CreateAsync(intent, Authority(github), CancellationToken.None);
        github.IssueBody = "Human edited this issue; factory must never restore its payload.";
        var repeated = await Publisher(http).CreateAsync(intent, Authority(github), CancellationToken.None);

        Assert.Equal(FactoryPublicationState.Confirmed, result.State);
        Assert.Equal(result.Receipt, repeated.Receipt);
        Assert.Equal(913, result.Receipt!.RepositoryId);
        Assert.Equal(97001, result.Receipt.IssueId);
        Assert.Equal(17, result.Receipt.Number);
        Assert.Equal(97001, JsonNode.Parse(result.Receipt.ResponseJson)!["id"]!.GetValue<long>());
        Assert.NotNull(result.NativeIdentity);
        var nativeReceipt = native.Documents[FactoryPublication.Id(intent)];
        Assert.Equal(FactoryAudit.TextHash(nativeReceipt), result.NativeIdentity.Sha256);
        Assert.Equal((string?)JsonNode.Parse(nativeReceipt)!["_etag"], result.NativeIdentity.NativeRevision);
        Assert.Single(github.IssueCreates);
        Assert.Equal("Human edited this issue; factory must never restore its payload.", github.IssueBody);
        Assert.DoesNotContain(AuthorityTransport.InstallationToken, native.Export());
        Assert.DoesNotContain("PRIVATE KEY", native.Export());
        Assert.DoesNotContain(github.Paths, p => p.Contains("/issues/17", StringComparison.Ordinal)
            || p.Contains("/search", StringComparison.Ordinal) || p.Contains("/comments", StringComparison.Ordinal));
        var changed = intent with { InvocationId = Guid.NewGuid(), Title = "Later candidate words", Body = "Different body" };
        var sameIntent = await Publisher(http).CreateAsync(changed, Authority(github), CancellationToken.None);
        Assert.Equal(result.Receipt, sameIntent.Receipt);
        Assert.Single(github.IssueCreates);
    }

    [Theory]
    [InlineData("lost")]
    [InlineData("canceled")]
    [InlineData("server-error")]
    [InlineData("wrong-repository")]
    [InlineData("malformed")]
    public async Task Uncertain_create_response_is_quarantined_without_search_or_second_create(string damage)
    {
        using var native = new PublicationTransport();
        using var github = new AuthorityTransport { PublicationEnabled = true, IssueFailure = damage };
        using var remote = new PublicationWorkflowTransport(native, github);
        using var http = new HttpClient(remote);
        var intent = FactoryPublicationJournalTests.Intent();

        var result = await Publisher(http).CreateAsync(intent, Authority(github), CancellationToken.None);
        using var restarted = new PublicationTransport(native.Export());
        using var nextRemote = new PublicationWorkflowTransport(restarted, github);
        using var nextHttp = new HttpClient(nextRemote);
        var next = await Publisher(nextHttp).CreateAsync(intent, Authority(github), CancellationToken.None);

        Assert.Equal(FactoryPublicationState.Unknown, result.State);
        Assert.Equal(FactoryPublicationState.Unknown, next.State);
        Assert.Null(next.Receipt);
        Assert.Single(github.IssueCreates);
        Assert.DoesNotContain(github.Paths, p => p.Contains("/search", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Exact_known_rejection_can_retry_but_preserves_the_original_payload_and_attempt()
    {
        using var native = new PublicationTransport();
        using var github = new AuthorityTransport { PublicationEnabled = true, IssueFailure = "rejected" };
        using var remote = new PublicationWorkflowTransport(native, github);
        using var http = new HttpClient(remote);
        var intent = FactoryPublicationJournalTests.Intent();

        var rejected = await Publisher(http).CreateAsync(intent, Authority(github), CancellationToken.None);
        github.IssueFailure = null;
        var confirmed = await Publisher(http).CreateAsync(intent, Authority(github), CancellationToken.None);

        Assert.Equal(FactoryPublicationState.Rejected, rejected.State);
        Assert.Equal(FactoryPublicationState.Confirmed, confirmed.State);
        Assert.Equal(2, confirmed.Attempts.Count);
        Assert.Equal(rejected.Attempts[0], confirmed.Attempts[0]);
        Assert.Equal(2, github.IssueCreates.Count);
        Assert.Equal(github.IssueCreates[0], github.IssueCreates[1]);
    }

    [Theory]
    [InlineData("workflow")]
    [InlineData("hook")]
    [InlineData("label")]
    [InlineData("missing-permission")]
    public async Task Current_mutation_inventory_or_effective_scope_blocks_before_create(string damage)
    {
        using var native = new PublicationTransport();
        using var github = new AuthorityTransport { PublicationEnabled = damage != "missing-permission", PublicationConflict = damage };
        using var remote = new PublicationWorkflowTransport(native, github);
        using var http = new HttpClient(remote);

        await Assert.ThrowsAnyAsync<InvalidOperationException>(() => Publisher(http).CreateAsync(
            FactoryPublicationJournalTests.Intent(), Authority(github), CancellationToken.None));

        Assert.Empty(github.IssueCreates);
        Assert.DoesNotContain(native.Documents.Values, d => (string?)JsonNode.Parse(d)!["state"] == "Dispatched");
    }

    private static FactoryGitHubProposalPublisher Publisher(HttpClient http) =>
        new(new PublicationCredential(), http, new AuthorityClock());

    private static FactoryGitHubAuthority Authority(AuthorityTransport github) =>
        github.Authority.Deserialize<FactoryGitHubAuthority>(FactoryGitHubJson.Options)!;
}

internal sealed class PublicationWorkflowTransport(PublicationTransport native, AuthorityTransport github) : HttpMessageHandler
{
    private readonly HttpMessageInvoker nativeHttp = new(native, disposeHandler: false);
    private readonly HttpMessageInvoker githubHttp = new(github, disposeHandler: false);
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) =>
        request.RequestUri!.Host.EndsWith(".documents.azure.com", StringComparison.Ordinal)
            ? nativeHttp.SendAsync(request, token) : githubHttp.SendAsync(request, token);
    protected override void Dispose(bool disposing)
    {
        if (disposing) { nativeHttp.Dispose(); githubHttp.Dispose(); }
        base.Dispose(disposing);
    }
}
