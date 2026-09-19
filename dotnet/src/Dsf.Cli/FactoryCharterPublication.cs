using System.Text.Json;
using Azure.Data.AppConfiguration;
using Dsf.Core.Onboarding;
using Dsf.Core.Runtime;

namespace Dsf.Cli;

internal sealed partial class FactoryCharterService
{
    private const string PublicationKey = "dsf/factory/charter/publication";
    private const string PublicationPrefix = "dsf/factory/charter/publications/";
    private const string PublicationInitializedKey = PublicationPrefix + "initialized";
    private const int PayloadChunkSize = FactoryCharterAdoptionReader.PayloadChunkCharacters;
    private const int MaximumPayloadRecords = FactoryCharterLimits.MaximumPayloadRecords;
    private static string PayloadKey(Guid operation, int index) => $"{PublicationPrefix}{operation:N}/payload/{index:D4}";
    private static string PublicationHistoryKey(int sequence) => $"{PublicationPrefix}history/{sequence:D2}";

    private OwnerReservationStore ProductStore(FactoryReservationResult admission) => new(admission.Foundation!.ProductEndpoint!,
        admission.Identity!.TenantId, admission.Identity.SubscriptionId, azure, http, admission.Identity.AzureActorId);

    internal async Task<FactoryCharterResult> StatusAsync(FactoryReservationResult admission, CancellationToken token, bool publicationOnly = false)
    {
        var store = ProductStore(admission);
        var root = await store.ReadAsync(PublicationKey, token);
        if (root is null) await store.RequirePristineAsync(PublicationPrefix, token);
        if (!publicationOnly && await AdoptionStatusAsync(admission, token) is { } adopted)
        {
            var pending = root is null ? null : ReadPublication(root, admission);
            if (pending is not null) _ = await ReadPayloadAsync(store, pending, token);
            return adopted with             { Publication = pending is null ? null : PublicPublication(pending) };
        }
        if (root is null) return await PreviewAsync(admission, false, token);
        var publication = ReadPublication(root, admission);
        var payload = await ReadPayloadAsync(store, publication, token);
        var state = publication.Effects.Any(e => e.State is CharterEffectState.Dispatched or CharterEffectState.Unknown)
            ? FactoryCharterState.Unknown : publication.State;
        try { _ = await PreviewAsync(admission, false, token); }
        catch (CharterOperationException e)
        {
            return new(e.State, e.Message + " Publication receipts retained as history; no current authority.")
                { Publication = PublicPublication(publication), Revision = root.ETag.ToString(), BlockedOperation = "Adoption/publication" };
        }
        return new(state, state == FactoryCharterState.Unknown
            ? "Publication quarantined. Original native receipts required; no search-marker adoption, recreation or forced retry."
            : "Publication receipt history; pending PR is not governing intent.", payload.Review)
        {
            Publication = PublicPublication(publication), Revision = root.ETag.ToString(),
            NextAction = state == FactoryCharterState.Published ? "Human reviews/merges the exact PR, then operator previews adoption."
                : "Inspect original durable operation and native receipts; no automatic takeover.",
        };
    }

    internal async Task<FactoryCharterResult> PublishAsync(FactoryReservationResult admission, FactoryCharterResult reviewed,
        string approval, CancellationToken token, Guid? retryOperation = null, string? retryRevision = null)
    {
        var review = reviewed.Review ?? throw new JsonException();
        if (approval != review.Fingerprint)
            throw new CharterOperationException(FactoryCharterState.Blocked, "Approval differs from the current exact charter review; no effects.");
        if (review.Before == review.After || review.Existing)
            throw new CharterOperationException(FactoryCharterState.Blocked, "Unchanged charter: explicitly adopt existing intent; no empty PR.");
        var store = ProductStore(admission);
        var native = new OperatorCharterGitHub(reviewer.OperatorGitHub);
        var root = await store.ReadAsync(PublicationKey, token);
        if (retryOperation is not null && root is null)
            throw new CharterOperationException(FactoryCharterState.Blocked, "Original rejected operation required; retry cannot create replacement intent.");
        if (root is null) await store.RequirePristineAsync(PublicationPrefix, token);
        CharterPublication publication = null!;
        CharterPublication? preceding = null;
        var payload = new CharterPublicationPayload(review, reviewed.Evidence!);
        var serialized = FactoryReservationRecords.Serialize(payload);
        var parts = Enumerable.Range(0, (serialized.Length + PayloadChunkSize - 1) / PayloadChunkSize)
            .Select(i => serialized.Substring(i * PayloadChunkSize, Math.Min(PayloadChunkSize, serialized.Length - i * PayloadChunkSize))).ToArray();
        if (parts.Length is < 1 or > MaximumPayloadRecords)
            throw new CharterOperationException(FactoryCharterState.Partial, "Complete review exceeds bounded private publication budget; nothing truncated or dispatched.");
        if (root is not null)
        {
            publication = ReadPublication(root, admission);
            if (retryOperation is { } originalOperation)
            {
                RequireOriginalPublication(publication, root.ETag.ToString(), originalOperation, retryRevision!);
                if (publication.ReviewFingerprint != review.Fingerprint
                    || publication.Effects.LastOrDefault() is not { State: CharterEffectState.KnownFailed or CharterEffectState.NotDispatched })
                    throw new CharterOperationException(FactoryCharterState.Blocked, "Retry requires original unchanged review and native known-no-effect outcome.");
            }
            if (publication.ReviewFingerprint != review.Fingerprint)
            {
                var adopted = await AdoptionStatusAsync(admission, token);
                if (publication.State != FactoryCharterState.Published || publication.Effects.Count != 3
                    || publication.Effects.Any(e => e.State != CharterEffectState.Confirmed)
                    || adopted?.State != FactoryCharterState.Adopted || adopted.Adoption?.PublicationOperation != publication.Operation)
                    throw new CharterOperationException(FactoryCharterState.Blocked, "Different publication held; original PR must be human-merged and explicitly adopted before another proposal.");
                if (publication.Sequence >= FactoryCharterLimits.MaximumRevisions)
                    throw new CharterOperationException(FactoryCharterState.Partial, "Publication lifecycle limit; no new intent or effects.");
                _ = await ReadPayloadAsync(store, publication, token);
                preceding = publication;
            }
            else
            {
                if (publication.Effects.Any(e => e.State is CharterEffectState.Dispatched or CharterEffectState.Unknown)
                    || retryOperation is null && publication.Effects.Any(e => e.State is CharterEffectState.KnownFailed or CharterEffectState.NotDispatched))
                    return await StatusAsync(admission, token, publicationOnly: true);
                if (publication.State == FactoryCharterState.Published) return await StatusAsync(admission, token, publicationOnly: true);
                var original = await ReadPayloadAsync(store, publication, token);
                payload = original;
                serialized = FactoryReservationRecords.Serialize(original);
                if (retryOperation is not null)
                {
                    var failed = publication.Effects.Last();
                    if (publication.Effects.Sum(e => e.PreviousAttempts.Count + 1) >= MaximumCharterAttempts)
                        throw new CharterOperationException(FactoryCharterState.Partial, "Bounded original attempt history exhausted; no dispatch.");
                    await CheckCurrentAsync(admission, review, token);
                    publication = WithEffect(publication, failed with
                    {
                        State = CharterEffectState.Intended, Attempt = Guid.NewGuid(), Receipt = null, ObjectId = null, Response = null, ResponseRecord = null,
                        Reconciliation = null, PreviousAttempts = [.. failed.PreviousAttempts, new(failed.Attempt, failed.State, failed.Receipt)],
                    }) with { State = FactoryCharterState.Partial };
                }
            }
        }
        if (root is null || preceding is not null)
        {
            var operation = Guid.NewGuid();
            publication = new(1, operation, review.FactoryId, (preceding?.Sequence ?? 0) + 1, review.Fingerprint, parts.Length,
                FactoryModelsValidation.Hash(serialized), "refs/heads/dsf/charter/" + operation.ToString("N"), clock.GetUtcNow(),
                review.OperatorId, FactoryCharterState.Partial, [], null) { PreviousOperation = preceding?.Operation };
            CheckPublicationPayloadCapacity(publication, parts);
            native.CheckBase(review, token);
            if (native.Head(review, publication.Branch["refs/heads/".Length..], token) is not null)
                throw new CharterOperationException(FactoryCharterState.Blocked, "Proposed branch already exists; no adoption or overwrite.");
            await CheckCurrentAsync(admission, review, token);
            await new FactoryAuditService(azure, http, clock).ProtectAnalysisAsync(admission, review, token);
            if (preceding is not null)
            {
                await FencePublicationAsync(store, root!, token);
                var key = PublicationHistoryKey(preceding.Sequence);
                var historical = await store.ReadAsync(key, token);
                if (historical is not null && historical.Value != root!.Value) throw new JsonException();
                if (historical is null) await store.WriteAsync(key, root!.Value, null, token);
            }
            else await store.WriteAsync(PublicationInitializedKey, FactoryReservationRecords.Serialize(publication), null, token);
            root = await store.WriteAsync(PublicationKey, FactoryReservationRecords.Serialize(publication), root?.ETag, token);
            for (var i = 0; i < parts.Length; i++)
            {
                await FencePublicationAsync(store, root, token);
                await store.WriteAsync(PayloadKey(operation, i), FactoryReservationRecords.Serialize(parts[i]), null, token);
            }
        }
        var issued = false;
        var acknowledgedDispatch = false;
        try
        {
            for (var i = publication.Effects.TakeWhile(e => e.State == CharterEffectState.Confirmed).Count(); i < 3; i++)
            {
                var (kind, route, body) = EffectRequest(i, review, publication);
                issued = false;
                acknowledgedDispatch = false;
                var intent = new CharterEffect(i, kind, CharterEffectState.Intended, FactoryModelsValidation.Hash(body)) { Attempt = Guid.NewGuid() };
                if (publication.Effects.Count == i)
                {
                    if (publication.Effects.Sum(e => e.PreviousAttempts.Count + 1) >= MaximumCharterAttempts)
                        throw new CharterOperationException(FactoryCharterState.Partial, "Bounded original attempt history exhausted; no next dispatch.");
                    publication = publication with { Effects = [.. publication.Effects, intent] };
                }
                else
                {
                    if (publication.Effects.Count != i + 1 || publication.Effects[i] is not { State: CharterEffectState.Intended } current
                        || current.Kind != kind || current.RequestSha256 != intent.RequestSha256) throw new JsonException();
                    intent = current.Attempt == Guid.Empty ? current with { Attempt = Guid.NewGuid() } : current;
                    publication = WithEffect(publication, intent);
                }
                CheckPublicationCapacity(publication, review);
                root = await SavePublicationAsync(store, root!, publication, token);
                await CheckCurrentAsync(admission, review, token);
                native.CheckBase(review, token);
                if (i == 0 && native.Head(review, publication.Branch["refs/heads/".Length..], token) is not null)
                    throw new CharterOperationException(FactoryCharterState.Blocked, "Branch appeared before creation; no inferred ownership or overwrite.");
                if (i == 1) native.CheckEmptyBranch(review, publication, token);
                if (i == 2) native.CheckProposal(review, publication, token);
                publication = WithEffect(publication, intent with { State = CharterEffectState.Dispatched });
                root = await SavePublicationAsync(store, root, publication, token);
                acknowledgedDispatch = true;
                // A conditional record coordinates DSF workers, not GitHub actors. No ref update or lease takeover exists.
                await CheckCurrentAsync(admission, review, token);
                native.CheckBase(review, token);
                if (i == 1) native.CheckEmptyBranch(review, publication, token);
                if (i == 2) native.CheckProposal(review, publication, token);
                if (root.ETag.ToString().Length > MaximumReconciliationRevisionCharacters)
                    throw new CharterOperationException(FactoryCharterState.Partial, "Native revision exceeds reserved reconciliation metadata; no GitHub dispatch.");
                issued = true;
                var response = native.Request("POST", route, body, token);
                if (response.Status != 200 || response.Body.TryGetProperty("errors", out _))
                {
                    var rejected = response.Status is 400 or 401 or 403 or 404 or 422 && response.Receipt.RequestId.Length > 0
                        && !response.Body.TryGetProperty("data", out var data);
                    publication = WithEffect(publication, intent with { State = rejected ? CharterEffectState.KnownFailed : CharterEffectState.Unknown,
                        Receipt = response.Receipt }) with { State = rejected ? FactoryCharterState.Failed : FactoryCharterState.Unknown };
                    using var closeout = new CancellationTokenSource(TimeSpan.FromSeconds(30), clock);
                    root = await SavePublicationAsync(store, root, publication, closeout.Token);
                    return Result(publication, root, "GitHub write rejected or uncertain; partial effects retained, no automatic retry.");
                }
                if (response.Receipt.RequestId.Length == 0) throw new JsonException();
                var witnessed = intent with { State = CharterEffectState.Dispatched, Response = new(publication.FactoryId,
                    review.RepositoryId, publication.Operation, intent.Attempt, review.Fingerprint, intent.RequestSha256, response.Receipt, response.ResponseJson) };
                var confirmed = ConfirmResponse(publication, review, witnessed);
                if (i == 2 && confirmed.PullRequest!.NodeId == preceding?.PullRequest?.NodeId) throw new JsonException();
                // Only this acknowledged dispatcher can retain the original response. Recovery accepts no caller-supplied bytes.
                using var completion = new CancellationTokenSource(TimeSpan.FromSeconds(30), clock);
                await FencePublicationAsync(store, root, completion.Token);
                var savedResponse = await store.WriteAsync(FactoryCharterResponses.Key(publication.Operation, intent.Attempt),
                    FactoryReservationRecords.Serialize(FactoryCharterResponses.Encode(witnessed.Response!)), null, completion.Token);
                var retained = confirmed.Effects[i] with
                {
                    Response = null, ResponseRecord = new(FactoryAudit.TextHash(savedResponse.ETag.ToString()), FactoryAudit.TextHash(savedResponse.Value)),
                };
                publication = WithEffect(publication, retained with { State = CharterEffectState.Dispatched });
                confirmed = WithEffect(confirmed, retained);
                root = await SavePublicationAsync(store, root, publication, completion.Token);
                root = await SavePublicationAsync(store, root, confirmed, completion.Token);
                publication = confirmed;
            }
            await CheckCurrentAsync(admission, review, token);
            native.CheckBase(review, token);
            native.CheckProposal(review, publication, token);
            return Result(publication, root, "Exact charter-only PR published using effective gh identity; human merge/adoption outstanding. Factory inactive.");
        }
        catch (OperationCanceledException)
        {
            await RetainNoDispatchAsync();
            return Result(publication, root, "Canceled; dispatched effects may have committed. No rollback or replay.", FactoryCharterState.Canceled);
        }
        catch (Exception e) when (e is CharterOperationException or ReservationStoreException or JsonException or InvalidOperationException
            or HttpRequestException or IOException or KeyNotFoundException or FormatException)
        {
            await RetainNoDispatchAsync();
            var state = publication.Effects.Any(e => e.State == CharterEffectState.Dispatched) ? FactoryCharterState.Unknown
                : e is CharterOperationException problem ? problem.State : FactoryCharterState.Unknown;
            if (!issued && state == FactoryCharterState.Partial && e is CharterOperationException capacity)
                return Result(ReadPublication(root, admission), root, capacity.Message, state);
            return Result(publication, root, "Publication held; native response or current authority unconfirmed. Inspect original receipts; no recreation.", state);
        }

        async Task RetainNoDispatchAsync()
        {
            if (issued || !acknowledgedDispatch) return;
            try
            {
                var notIssued = WithEffect(publication, publication.Effects.Last() with { State = CharterEffectState.NotDispatched });
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(30), clock);
                root = await SavePublicationAsync(store, root!, notIssued, cleanup.Token);
                publication = notIssued;
            }
            catch (Exception e) when (e is ReservationStoreException or JsonException or OperationCanceledException or HttpRequestException) { }
        }
    }

    private static CharterPublication WithEffect(CharterPublication publication, CharterEffect effect) =>
        publication with { Effects = publication.Effects.Select(e => e.Index == effect.Index ? effect : e).ToArray() };

    private static (string Kind, string Route, object Body) EffectRequest(int index, CharterReview review, CharterPublication p) => index switch
    {
        0 => ("createRef", "graphql", new
        {
            query = "mutation($input:CreateRefInput!){createRef(input:$input){ref{id name repository{id} target{oid}}}}",
            variables = new { input = new { repositoryId = review.RepositoryNodeId, name = p.Branch, oid = review.Commit } },
        }),
        1 => ("createCommitOnBranch", "graphql", new
        {
            query = "mutation($input:CreateCommitOnBranchInput!){createCommitOnBranch(input:$input){commit{oid tree{oid} parents(first:2){nodes{oid}}}}}",
            variables = new { input = new { branch = new { id = p.Effects[0].ObjectId }, expectedHeadOid = review.Commit,
                message = new { headline = "docs: propose reviewed product charter" },
                fileChanges = new { additions = new[] { new { path = review.Path,
                    contents = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(review.After)) } } } } },
        }),
        2 => ("createPullRequest", "graphql", new
        {
            query = "mutation($input:CreatePullRequestInput!){createPullRequest(input:$input){pullRequest{id number url repository{id} baseRefName baseRefOid headRefName headRefOid state merged}}}",
            variables = new { input = new { repositoryId = review.RepositoryNodeId, title = "Review product charter",
                headRefName = p.Branch["refs/heads/".Length..], baseRefName = review.DefaultBranch,
                body = "Operator-reviewed product charter only. Human review and merge required. No activation or delivery assignment.",
                maintainerCanModify = false } },
        }),
        _ => throw new JsonException(),
    };

    private static string Confirm(int index, CharterReview review, CharterPublication p, JsonElement json)
    {
        if (index == 0)
        {
            var reference = json.GetProperty("ref");
            if (reference.GetProperty("name").GetString() != p.Branch["refs/heads/".Length..]
                || reference.GetProperty("repository").GetProperty("id").GetString() != review.RepositoryNodeId
                || reference.GetProperty("target").GetProperty("oid").GetString() != review.Commit) throw new JsonException();
            return reference.GetProperty("id").GetString() is { Length: > 0 and <= 128 } node ? node : throw new JsonException();
        }
        if (index == 1)
        {
            var commit = json.GetProperty("commit");
            _ = OperatorCharterGitHub.GitId(commit.GetProperty("tree").GetProperty("oid"));
            if (commit.GetProperty("parents").GetProperty("nodes").GetArrayLength() != 1
                || commit.GetProperty("parents").GetProperty("nodes")[0].GetProperty("oid").GetString() != review.Commit) throw new JsonException();
            return OperatorCharterGitHub.GitId(commit.GetProperty("oid"));
        }
        return PullReceipt(review, p, json.GetProperty("pullRequest")).NodeId;
    }

    private static CharterPullReceipt PullReceipt(CharterReview review, CharterPublication p, JsonElement json)
    {
        var receipt = new CharterPullReceipt(null, json.GetProperty("id").GetString()!,
            json.GetProperty("number").GetInt32(), json.GetProperty("url").GetString()!, review.RepositoryId,
            json.GetProperty("baseRefName").GetString()!, json.GetProperty("baseRefOid").GetString()!,
            json.GetProperty("headRefName").GetString()!, json.GetProperty("headRefOid").GetString()!);
        if (receipt.Number <= 0 || receipt.NodeId.Length is < 1 or > 128
            || receipt.Url != $"https://github.com/{review.Repository}/pull/{receipt.Number}"
            || json.GetProperty("repository").GetProperty("id").GetString() != review.RepositoryNodeId
            || receipt.Base != review.DefaultBranch || receipt.BaseCommit != review.Commit
            || receipt.Head != p.Branch["refs/heads/".Length..] || receipt.HeadCommit != p.Effects[1].ObjectId
            || json.GetProperty("state").GetString() != "OPEN" || json.GetProperty("merged").ValueKind != JsonValueKind.False)
            throw new JsonException();
        return receipt;
    }

    private async Task CheckCurrentAsync(FactoryReservationResult admission, CharterReview expected, CancellationToken token)
    {
        var stops = await ProductStore(admission).ReadNamespaceAsync(FactoryPause.Key, token);
        var history = await ProductStore(admission).ReadNamespaceAsync(FactoryPause.HistoryPrefix, token);
        if (stops.Count != 0 || history.Count != 0)
            throw new CharterOperationException(FactoryCharterState.Blocked, "Stop closes all new charter dispatch, not original outcome inspection.");
        var fresh = await new FactoryReservationService(reviewer, azure, http, clock).StatusAsync(admission.Identity!.OwnerEndpoint,
            admission.Identity.TenantId, admission.Identity.SubscriptionId, admission.Identity.ProductKey, token);
        if (fresh.State != FactoryReservationState.Reserved || fresh.Identity != admission.Identity || fresh.Foundation?.State != FactoryFoundationState.Inactive)
            throw new CharterOperationException(FactoryCharterState.Blocked, "Current owner-registry authority changed.");
        var current = await PreviewAsync(fresh, expected.Existing, token);
        if (current.Revision != expected.Fingerprint)
            throw new CharterOperationException(FactoryCharterState.Blocked, "Current review changed; explicit review required before any next effect.");
    }

    private static CharterPublication ReadPublication(ConfigurationSetting root, FactoryReservationResult admission)
    {
        if (root.ContentType != OwnerReservationStore.ContentType) throw new JsonException();
        var p = JsonSerializer.Deserialize<CharterPublication>(root.Value, FactorySourceValidation.RecordJson) ?? throw new JsonException();
        if (p.Protocol != 1 || p.FactoryId != admission.Identity!.FactoryId || p.Operation == Guid.Empty
            || p.Sequence is < 1 or > FactoryCharterLimits.MaximumRevisions || p.PayloadRecords is < 1 or > MaximumPayloadRecords
            || (p.Sequence == 1) != (p.PreviousOperation is null)
            || p.Branch != "refs/heads/dsf/charter/" + p.Operation.ToString("N") || p.Effects.Count > 3
            || p.Effects.Sum(e => e.PreviousAttempts.Count + 1) > MaximumCharterAttempts
            || !FactorySourceValidation.Hash(p.ReviewFingerprint) || !FactorySourceValidation.Hash(p.PayloadSha256)
            || p.Effects.Where((e, index) => e.Index != index || !Enum.IsDefined(e.State)
                || index < p.Effects.Count - 1 && e.State != CharterEffectState.Confirmed).Any()) throw new JsonException();
        return p;
    }
    private static async Task<CharterPublicationPayload> ReadPayloadAsync(OwnerReservationStore store, CharterPublication p, CancellationToken token)
    {
        var initialization = await store.ReadAsync(PublicationInitializedKey, token) ?? throw new JsonException();
        var initial = JsonSerializer.Deserialize<CharterPublication>(initialization.Value, FactorySourceValidation.RecordJson) ?? throw new JsonException();
        if (initialization.ContentType != OwnerReservationStore.ContentType || initial.Protocol != 1 || initial.Sequence != 1
            || initial.FactoryId != p.FactoryId || initial.Effects.Count != 0 || initial.PreviousOperation is not null)
            throw new JsonException();
        var previous = p.PreviousOperation;
        var operations = new HashSet<Guid> { p.Operation };
        var first = p;
        for (var i = p.Sequence - 1; i >= 1; i--)
        {
            var record = await store.ReadAsync(PublicationHistoryKey(i), token) ?? throw new JsonException();
            var historical = JsonSerializer.Deserialize<CharterPublication>(record.Value, FactorySourceValidation.RecordJson) ?? throw new JsonException();
            if (historical.Operation != previous || historical.Sequence != i || historical.FactoryId != p.FactoryId
                || historical.State != FactoryCharterState.Published || historical.Effects.Count != 3
                || historical.Effects.Any(e => e.State != CharterEffectState.Confirmed) || !operations.Add(historical.Operation))
                throw new JsonException();
            previous = historical.PreviousOperation;
            first = historical;
        }
        if (previous is not null || first.Operation != initial.Operation || first.ReviewFingerprint != initial.ReviewFingerprint
            || first.PayloadSha256 != initial.PayloadSha256 || first.Branch != initial.Branch) throw new JsonException();
        var text = new System.Text.StringBuilder();
        for (var i = 0; i < p.PayloadRecords; i++)
        {
            var part = await store.ReadAsync(PayloadKey(p.Operation, i), token) ?? throw new JsonException();
            if (part.ContentType != OwnerReservationStore.ContentType) throw new JsonException();
            var value = JsonSerializer.Deserialize<string>(part.Value, FactorySourceValidation.RecordJson) ?? throw new JsonException();
            if (value.Length is < 1 or > PayloadChunkSize) throw new JsonException();
            text.Append(value);
        }
        if (FactoryModelsValidation.Hash(text.ToString()) != p.PayloadSha256) throw new JsonException();
        var payload = JsonSerializer.Deserialize<CharterPublicationPayload>(text.ToString(), FactorySourceValidation.RecordJson) ?? throw new JsonException();
        if (payload.Review.Fingerprint != p.ReviewFingerprint || payload.Review.FactoryId != p.FactoryId) throw new JsonException();
        foreach (var effect in p.Effects)
        {
            var (kind, route, body) = EffectRequest(effect.Index, payload.Review, p);
            if (effect.Kind != kind || effect.RequestSha256 != FactoryModelsValidation.Hash(body)
                || effect.State == CharterEffectState.Confirmed && (effect.ObjectId is not { Length: > 0 and <= 128 }
                    || effect.Receipt is not { Method: "POST", Status: 200, RequestId.Length: > 0 and <= 128 } receipt
                    || receipt.Route != route || !FactorySourceValidation.Hash(receipt.ResponseSha256)))
                throw new JsonException();
            if (effect.PreviousAttempts.Count > MaximumCharterAttempts
                || effect.PreviousAttempts.Any(a => a.State is not (CharterEffectState.KnownFailed or CharterEffectState.NotDispatched)
                    || a.State == CharterEffectState.KnownFailed && !KnownRejection(a.Native))
                || effect.State == CharterEffectState.KnownFailed && !KnownRejection(effect.Receipt))
                throw new JsonException();
            if (await ReadResponseAsync(store, p, effect, token) is { } response)
            {
                if (effect.State is not (CharterEffectState.Dispatched or CharterEffectState.Unknown or CharterEffectState.Confirmed))
                    throw new JsonException();
                var confirmed = ConfirmResponse(p, payload.Review, effect with { Response = response }).Effects[effect.Index];
                if ((effect.State == CharterEffectState.Confirmed || effect.ResponseRecord is not null)
                    && (effect.ObjectId != confirmed.ObjectId || effect.Receipt != confirmed.Receipt))
                    throw new JsonException();
            }
        }
        if (p.State == FactoryCharterState.Published && (p.Effects.Count != 3 || p.Effects.Any(e => e.State != CharterEffectState.Confirmed)
            || p.PullRequest is not { } pull || pull.NodeId != p.Effects[2].ObjectId || pull.RepositoryId != payload.Review.RepositoryId
            || pull.Base != payload.Review.DefaultBranch || pull.BaseCommit != payload.Review.Commit
            || pull.Head != p.Branch["refs/heads/".Length..] || pull.HeadCommit != p.Effects[1].ObjectId))
            throw new JsonException();
        return payload;
    }
    private static async Task FencePublicationAsync(OwnerReservationStore store, ConfigurationSetting root, CancellationToken token)
    {
        var current = await store.ReadAsync(root.Key, token);
        if (current?.ETag != root.ETag || current.Value != root.Value) throw new JsonException();
    }
    private static async Task<ConfigurationSetting> SavePublicationAsync(OwnerReservationStore store, ConfigurationSetting root,
        CharterPublication p, CancellationToken token)
    {
        await FencePublicationAsync(store, root, token);
        return await store.WriteAsync(root.Key, FactoryReservationRecords.Serialize(p), root.ETag, token);
    }
    private static FactoryCharterResult Result(CharterPublication p, ConfigurationSetting root, string detail, FactoryCharterState? state = null) =>
        new(state ?? p.State, detail) { Publication = PublicPublication(p), Revision = root.ETag.ToString() };

    private static bool KnownRejection(CharterNativeReceipt? receipt) =>
        receipt is { Method: "POST", Route: "graphql", Status: 400 or 401 or 403 or 404 or 422, RequestId.Length: > 0 and <= 128 }
        && FactorySourceValidation.Hash(receipt.ResponseSha256);

    private static async Task<CharterResponseWitness?> ReadResponseAsync(OwnerReservationStore store, CharterPublication publication,
        CharterEffect effect, CancellationToken token)
    {
        if (effect.ResponseRecord is null) return effect.Response;
        var record = await store.ReadAsync(FactoryCharterResponses.Key(publication.Operation, effect.Attempt), token) ?? throw new JsonException();
        if (record.ContentType != OwnerReservationStore.ContentType) throw new JsonException();
        return FactoryCharterResponses.Read(publication, effect, record.Value, record.ETag.ToString());
    }
}
