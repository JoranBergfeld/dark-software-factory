using System.Text.Json;
using System.Text.Json.Nodes;
using Dsf.Core.Onboarding;
using Dsf.Core.Runtime;
using Xunit;
using CheckpointTransport = Dsf.Runtime.Tests.PublicationTransport;
using CheckpointCredential = Dsf.Runtime.Tests.PublicationCredential;

namespace Dsf.Runtime.Tests;

public sealed class FactoryPublicationJournalTests
{
    [Fact]
    public async Task Native_publication_audit_links_original_intent_inputs_and_witness_by_exact_revision()
    {
        var fixture = AuthorityTransport.ReadFixture();
        var manifest = JsonSerializer.Deserialize<FactoryManifest>(fixture[ProductConfigurationKeys.FactoryManifest],
            FactoryGitHubJson.Options)! with { GitHubAuthorityOperationId = null, GitHubAuthorityApprovalFingerprint = null };
        fixture[ProductConfigurationKeys.FactoryManifest] = JsonSerializer.Serialize(manifest, FactoryGitHubJson.Options);
        var records = fixture.Where(p => p.Key is ProductConfigurationKeys.FactoryManifest or ProductConfigurationKeys.FactoryProfile
                || p.Key.StartsWith("dsf/factory/reviewed-plan/", StringComparison.Ordinal))
            .Select(p => Record(AuditStoreKind.Governance, p.Key, p.Value)).ToList();
        var reviewed = Record(AuditStoreKind.Governance, "dsf/factory/audit/exact-input", """{"revision":"original"}""");
        records.Add(reviewed);
        var intent = Intent() with { References = [reviewed.Identity] };
        using var native = new CheckpointTransport();
        using var http = new HttpClient(native);
        var journal = Journal(http, intent);
        await journal.OpenAsync(CancellationToken.None);
        records.AddRange(native.Documents.Select(p => Record(AuditStoreKind.Publication,
            $"dbs/acme/colls/{FactoryPublication.Container}/docs/{p.Key}", p.Value)));

        var graph = FactoryAudit.Build(intent.FactoryId, records, DateTimeOffset.UtcNow);
        var publication = graph.Records.Single(r => r.Identity == journal.NativeIdentity);

        Assert.Contains(reviewed.Identity, publication.References);
        Assert.Contains(journal.Document.Origin, publication.References);
        var changed = reviewed with { Identity = reviewed.Identity with { NativeRevision = "\"new-version\"" } };
        Assert.Throws<JsonException>(() => FactoryAudit.Build(intent.FactoryId,
            records.Select(r => r == reviewed ? changed : r).ToArray(), DateTimeOffset.UtcNow));

        FactoryAuditRecord Record(AuditStoreKind store, string key, string content)
        {
            var body = JsonNode.Parse(content)!;
            var identity = new AuditRecordIdentity(manifest.Identity.FactoryId, store, key,
                store == AuditStoreKind.Publication ? (string)body["_etag"]! : "\"fixture-version\"", FactoryAudit.TextHash(content))
                { NativeResourceId = store == AuditStoreKind.Publication ? (string?)body["_rid"] : null };
            return new(identity, DateTimeOffset.UtcNow.AddSeconds(-1), content, [], AuditRetentionStatus.Retained);
        }
    }

    [Fact]
    public async Task Missing_journal_with_original_native_creation_witness_cannot_be_recreated()
    {
        using var native = new CheckpointTransport();
        using var http = new HttpClient(native);
        var intent = Intent();
        await Journal(http, intent).OpenAsync(CancellationToken.None);
        native.Documents.Remove(FactoryPublication.Id(intent));
        var writes = native.Writes.Count;

        await Assert.ThrowsAsync<InvalidOperationException>(() => Journal(http, intent).OpenAsync(CancellationToken.None));

        Assert.Equal(writes, native.Writes.Count);
    }

    [Fact]
    public async Task Native_intent_is_durable_before_dispatch_and_two_contenders_cannot_both_claim()
    {
        using var native = new CheckpointTransport();
        using var http = new HttpClient(native);
        var intent = Intent();
        var first = Journal(http, intent);
        var second = Journal(http, intent);

        Assert.Equal(FactoryPublicationState.Intended, (await first.OpenAsync(CancellationToken.None)).State);
        Assert.Equal(FactoryPublicationState.Intended, (await second.OpenAsync(CancellationToken.None)).State);
        var dispatched = await first.DispatchAsync(CancellationToken.None);
        await Assert.ThrowsAsync<CosmosCheckpointException>(() => second.DispatchAsync(CancellationToken.None));

        Assert.Equal(FactoryPublicationState.Dispatched, dispatched.State);
        Assert.Equal(FactoryModelsValidation.Hash(intent), FactoryModelsValidation.Hash(first.Document.Intent));
        Assert.DoesNotContain(native.Writes, w => w.Upsert);
        Assert.Equal(FactoryPublicationState.Dispatched, (await Journal(http, intent).OpenAsync(CancellationToken.None)).State);
    }

    [Fact]
    public async Task Logical_identity_excludes_invocation_and_refuses_changed_payload_under_the_original_key()
    {
        using var native = new CheckpointTransport();
        using var http = new HttpClient(native);
        var intent = Intent();
        var first = Journal(http, intent);
        await first.OpenAsync(CancellationToken.None);
        var changed = intent with { Title = "New model words cannot replace original intent", InvocationId = Guid.NewGuid() };
        Assert.Equal(FactoryPublication.Id(intent), FactoryPublication.Id(changed));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Journal(http, changed).OpenAsync(CancellationToken.None));
        Assert.Equal(2, native.Writes.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Issued_or_unknown_dispatch_survives_serialized_restart_without_another_claim(bool closeUnknown)
    {
        using var native = new CheckpointTransport();
        using var http = new HttpClient(native);
        var first = Journal(http, Intent());
        await first.OpenAsync(CancellationToken.None);
        await first.DispatchAsync(CancellationToken.None);
        if (closeUnknown) await first.UnknownAsync(CancellationToken.None);
        using var restarted = new CheckpointTransport(native.Export());
        using var restartedHttp = new HttpClient(restarted);
        var next = Journal(restartedHttp, Intent());

        Assert.Equal(closeUnknown ? FactoryPublicationState.Unknown : FactoryPublicationState.Dispatched,
            (await next.OpenAsync(CancellationToken.None)).State);
        await Assert.ThrowsAsync<InvalidOperationException>(() => next.DispatchAsync(CancellationToken.None));
        Assert.Empty(restarted.Writes);
    }

    [Fact]
    public async Task Known_rejection_allows_a_new_CAS_dispatch_with_original_payload_and_retained_attempt()
    {
        using var native = new CheckpointTransport();
        using var http = new HttpClient(native);
        var first = Journal(http, Intent());
        await first.OpenAsync(CancellationToken.None);
        var attempt = (await first.DispatchAsync(CancellationToken.None)).Attempts.Single();
        await first.RejectedAsync(422, "original-native-rejection", CancellationToken.None);
        var next = Journal(http, Intent());
        await next.OpenAsync(CancellationToken.None);

        var retried = await next.DispatchAsync(CancellationToken.None);

        Assert.Equal(2, retried.Attempts.Count);
        Assert.Equal(attempt.Id, retried.Attempts[0].Id);
        Assert.Equal(FactoryPublicationState.Rejected, retried.Attempts[0].State);
        Assert.NotEqual(attempt.Id, retried.Attempts[1].Id);
        Assert.Equal(FactoryModelsValidation.Hash(Intent()), FactoryModelsValidation.Hash(retried.Intent));
    }

    [Theory]
    [InlineData("missing-etag")]
    [InlineData("wrong-etag")]
    [InlineData("changed-echo")]
    public async Task Unconfirmed_native_intent_never_grants_a_dispatch_or_automatic_retry(string damage)
    {
        using var native = new CheckpointTransport { ResponseCorruption = damage };
        using var http = new HttpClient(native);
        var journal = Journal(http, Intent());
        await Assert.ThrowsAsync<CosmosCheckpointException>(() => journal.OpenAsync(CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(() => journal.DispatchAsync(CancellationToken.None));
        Assert.Single(native.Writes);
    }

    internal static FactoryPublicationIntent Intent() => new(1,
        Guid.Parse("db0a6eac-4211-4e3c-abe9-5ab62ba5929e"), "acme", 913, "factory/problem/payment-latency",
        Guid.Parse("22222222-2222-2222-2222-222222222222"), "Reduce payment latency",
        "Evidence-grounded proposal, not delivery assignment.", new string('a', 64), [
            new(Guid.Parse("db0a6eac-4211-4e3c-abe9-5ab62ba5929e"), AuditStoreKind.Preview,
                "dbs/acme/colls/council-preview/docs/live-reviewed", "\"preview-v1\"", new string('b', 64))
                { NativeResourceId = "AQIDBAUGBwgJCgsMDQ4PEA==" }
        ]) { AutomationReview = new(RepositoryMutationClass.ProposalIssue, RepositoryReviewState.Confirmed,
            RepositoryAutomationDeclaration.Clear, "original-automation-owner", true, ["Explicit original issue-only mutation review."]) };

    internal static FactoryPublicationJournal Journal(HttpClient http, FactoryPublicationIntent intent) =>
        new(intent, new CheckpointCredential(), http, TimeProvider.System);
}
