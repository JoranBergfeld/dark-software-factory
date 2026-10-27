using System.Text.Json;
using System.Text.Json.Nodes;
using Dsf.Core.Onboarding;
using Dsf.Core.Runtime;
using Xunit;

namespace Dsf.Cli.Tests;

public sealed class PreviewPublicationFailureTests
{
    [Fact]
    public async Task Competing_independent_cli_invocations_cannot_republish_or_duplicate_native_seed()
    {
        using var transport = await PreviewPublicationTests.PreparedAsync();
        var machine = transport.Scenario.Machine;
        var review = await machine.InvokeAsync("preview", "--configure");
        var approval = (string)review.Result["approvalFingerprint"]!;
        var entered = 0;
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        transport.Scenario.Arm.Product.BeforePut = async (key, create) =>
        {
            if (key != FactoryPreviewPublication.RootKey || !create) return;
            if (Interlocked.Increment(ref entered) == 2) release.TrySetResult();
            await release.Task.WaitAsync(TimeSpan.FromSeconds(30));
        };
        var results = await Task.WhenAll(machine.InvokeAsync("preview", "--configure", "--approve", approval),
            machine.InvokeAsync("preview", "--configure", "--approve", approval));
        Assert.Single(results, result => (string?)result.Result["state"] == "SeedAwaitingApproval");
        Assert.Single(results, result => (string?)result.Result["state"] == "Unknown");
        Assert.Equal(1, transport.SeedCreates);
        var retained = transport.Scenario.Arm.Product.Export();
        var inspected = await machine.InvokeAsync("preview", "--configure", "--status");
        Assert.Equal("SeedAwaitingApproval", (string?)inspected.Result["state"]);
        Assert.Equal(retained, transport.Scenario.Arm.Product.Export());
    }

    [Fact]
    public async Task Definitive_conditional_rejection_preserves_known_noeffect_accounting()
    {
        using var transport = await PreviewPublicationTests.PreparedAsync();
        var machine = transport.Scenario.Machine;
        var review = await machine.InvokeAsync("preview", "--configure");
        transport.Scenario.Arm.Product.RejectKey = ProductConfigurationKeys.FactoryPreview;
        var result = await machine.InvokeAsync("preview", "--configure", "--approve", (string)review.Result["approvalFingerprint"]!);
        Assert.Equal("KnownFailed", (string?)result.Result["state"]);
        Assert.Equal(0, transport.SeedCreates);
        var steps = transport.Values().Where(p => p.Key.StartsWith(FactoryPreviewPublication.Namespace, StringComparison.Ordinal)
            && p.Key != FactoryPreviewPublication.RootKey).Select(p => JsonNode.Parse(p.Value)!).ToArray();
        Assert.Contains(steps, step => (string?)step["target"] == ProductConfigurationKeys.FactoryPreview && (string?)step["state"] == "KnownFailed");
        var retained = transport.Scenario.Arm.Product.Export();
        var status = await machine.InvokeAsync("preview", "--configure", "--status");
        Assert.Equal("KnownFailed", (string?)status.Result["state"]);
        Assert.Equal(retained, transport.Scenario.Arm.Product.Export());
    }

    [Fact]
    public async Task Rejected_confirmation_does_not_reclassify_an_accepted_native_write_as_noeffect()
    {
        using var transport = await PreviewPublicationTests.PreparedAsync();
        var machine = transport.Scenario.Machine;
        var store = transport.Scenario.Arm.Product;
        var review = await machine.InvokeAsync("preview", "--configure");
        var rejectNext = false;
        store.AfterPut = (key, _) => rejectNext = key == ProductConfigurationKeys.FactoryPreview;
        store.BeforePut = (key, _) =>
        {
            store.RejectKey = rejectNext ? key : null;
            rejectNext = false;
            return Task.CompletedTask;
        };
        var result = await machine.InvokeAsync("preview", "--configure", "--approve", (string)review.Result["approvalFingerprint"]!);
        Assert.Equal("Unknown", (string?)result.Result["state"]);
        Assert.NotNull(transport.Values().GetValueOrDefault(ProductConfigurationKeys.FactoryPreview));
        var step = transport.Values().Where(p => p.Key.StartsWith(FactoryPreviewPublication.Namespace, StringComparison.Ordinal))
            .Select(p => JsonNode.Parse(p.Value)!).Single(p => (string?)p["target"] == ProductConfigurationKeys.FactoryPreview);
        Assert.Equal("Unknown", (string?)step["state"]);
        Assert.False(string.IsNullOrEmpty((string?)step["nativeRevision"]));
        transport.Scenario.Arm.Product = ReservationTestStore.Restore(store.Export());
        var retained = transport.Scenario.Arm.Product.Export();
        var status = await machine.InvokeAsync("preview", "--configure", "--status");
        Assert.Equal("Unknown", (string?)status.Result["state"]);
        Assert.Equal(retained, transport.Scenario.Arm.Product.Export());
        Assert.Equal(0, transport.SeedCreates);
    }

    [Fact]
    public async Task Complete_publication_capacity_is_checked_before_first_conditional_effect()
    {
        using var transport = await PreviewPublicationTests.PreparedAsync();
        var store = transport.Scenario.Arm.Product;
        var serialized = JsonNode.Parse(store.Export())!.AsObject();
        for (var i = 0; i < 1024; i++) serialized["dsf/factory/retained/" + i] =
            JsonSerializer.Serialize(new { key = "dsf/factory/retained/" + i, value = "retained", etag = "retained-" + i,
                label = (string?)null, content_type = "application/json", locked = false });
        transport.Scenario.Arm.Product = ReservationTestStore.Restore(serialized.ToJsonString());
        var retained = transport.Scenario.Arm.Product.Export();
        var reviewed = await transport.Scenario.Machine.InvokeAsync("preview", "--configure");
        Assert.Equal("Unknown", (string?)reviewed.Result["state"]);
        Assert.Equal(retained, transport.Scenario.Arm.Product.Export());
        Assert.Equal(0, transport.SeedCreates);
    }

    [Theory]
    [InlineData("lost-seed")]
    [InlineData("canceled-seed")]
    [InlineData("lost-capability")]
    [InlineData("lost-intent")]
    [InlineData("lost-final")]
    public async Task Unacknowledged_effects_remain_quarantined_across_serialized_clients(string failure)
    {
        using var transport = await PreviewPublicationTests.PreparedAsync();
        var machine = transport.Scenario.Machine;
        var store = transport.Scenario.Arm.Product;
        var review = await machine.InvokeAsync("preview", "--configure");
        var approval = (string)review.Result["approvalFingerprint"]!;
        if (failure is "lost-seed" or "canceled-seed") transport.SeedFailure = failure == "lost-seed" ? "lost" : "canceled";
        else if (failure == "lost-capability") store.LoseResponseForKey = ProductConfigurationKeys.FactoryPreview;
        else if (failure == "lost-intent") store.LoseResponseWhen = (key, value) =>
            key.StartsWith(FactoryPreviewPublication.Namespace, StringComparison.Ordinal) && (string?)JsonNode.Parse(value)?["state"] == "Intended";
        else
        {
            var seed = await machine.InvokeAsync("preview", "--configure", "--approve", approval);
            Assert.True(seed.Exit == 0, (string?)seed.Result["detail"]);
            approval = (string)seed.Result["approvalFingerprint"]!;
            store.LoseResponseForKey = ProductConfigurationKeys.FactoryPreview;
        }
        var failed = await machine.InvokeAsync("preview", "--configure", "--approve", approval);
        Assert.Equal("Unknown", (string?)failed.Result["state"]);
        Assert.False((bool)failed.Result["currentReady"]!);
        var creates = transport.SeedCreates;
        Assert.Equal(failure is "lost-capability" or "lost-intent" ? 0 : 1, creates);
        transport.Scenario.Arm.Product = ReservationTestStore.Restore(store.Export());
        var retained = transport.Scenario.Arm.Product.Export();
        var requests = transport.Requests.Count;
        var observed = await machine.InvokeAsync("preview", "--configure", "--status");
        Assert.Equal("Unknown", (string?)observed.Result["state"]);
        var resumed = await machine.InvokeAsync("preview", "--configure", "--approve", approval);
        Assert.Equal("Unknown", (string?)resumed.Result["state"]);
        Assert.Equal(retained, transport.Scenario.Arm.Product.Export());
        Assert.Equal(creates, transport.SeedCreates);
        Assert.DoesNotContain(transport.Requests.Skip(requests), r => r.Method is "POST" or "PUT" or "DELETE" or "PATCH");
        Assert.DoesNotContain("private-native", failed.Terminal.Output + failed.Terminal.Error);
    }

    [Theory]
    [InlineData("publication-root")]
    [InlineData("preparation-root")]
    [InlineData("publication-step")]
    [InlineData("publication-approval")]
    [InlineData("seed")]
    [InlineData("seed-rid")]
    [InlineData("grant-condition")]
    [InlineData("model-config")]
    [InlineData("optional-history")]
    public async Task Readonly_current_configuration_never_adopts_missing_or_changed_authority(string damage)
    {
        using var transport = await PreviewPublicationTests.PreparedAsync();
        var machine = transport.Scenario.Machine;
        var review = await machine.InvokeAsync("preview", "--configure");
        var seeded = await machine.InvokeAsync("preview", "--configure", "--approve", (string)review.Result["approvalFingerprint"]!);
        Assert.True(seeded.Exit == 0, (string?)seeded.Result["detail"]);
        var configured = await machine.InvokeAsync("preview", "--configure", "--approve", (string)seeded.Result["approvalFingerprint"]!);
        Assert.True(configured.Exit == 0, (string?)configured.Result["detail"]);
        var store = transport.Scenario.Arm.Product;
        if (damage == "publication-root") store.Remove(FactoryPreviewPublication.RootKey);
        else if (damage == "preparation-root") store.Remove(FactoryPreviewPreparation.RootKey);
        else if (damage == "publication-step")
            store.Remove(transport.Values().Keys.First(k => k.StartsWith(FactoryPreviewPublication.Namespace, StringComparison.Ordinal)
                && k.Contains("/seed/", StringComparison.Ordinal) && !k.EndsWith("/approval", StringComparison.Ordinal)));
        else if (damage == "publication-approval")
            store.Remove(FactoryPreviewPublication.ApprovalKey(transport.Manifest.PreviewOperationId!.Value, "seed"));
        else if (damage == "seed") transport.SeedDocuments.Clear();
        else if (damage == "seed-rid")
        {
            var key = transport.SeedDocuments.Keys.Single();
            var seed = JsonNode.Parse(transport.SeedDocuments[key])!;
            seed["_rid"] = Convert.ToBase64String("replacement"u8);
            transport.SeedDocuments[key] = seed.ToJsonString();
        }
        else if (damage == "grant-condition") transport.Resources[transport.NativeWrites[^1]]["properties"]!["condition"] = "unexpected";
        else if (damage == "model-config") store.Change(ProductConfigurationKeys.FactoryModels, raw =>
        {
            var record = JsonNode.Parse(raw)!;
            record["configuration"]!["runtimeImage"] = "registry.invalid/replaced@sha256:" + new string('f', 64);
            return record.ToJsonString();
        });
        else store.Change(ProductConfigurationKeys.FactoryManifest, raw =>
        {
            var record = JsonNode.Parse(raw)!;
            record["supplementalOperationId"] = Guid.NewGuid();
            return record.ToJsonString();
        });
        var retained = store.Export();
        var requests = transport.Requests.Count;
        var failed = await machine.InvokeAsync("preview", "--configure", "--status");
        Assert.NotEqual(0, failed.Exit);
        Assert.NotEqual("ConfiguredAwaitingEvaluation", (string?)failed.Result["state"]);
        Assert.False((bool)failed.Result["currentReady"]!);
        Assert.Equal(retained, store.Export());
        Assert.Equal(1, transport.SeedCreates);
        Assert.DoesNotContain(transport.Requests.Skip(requests), r => r.Method is "PUT" or "DELETE" or "PATCH" || r.Uri.AbsolutePath == "/prepare-evaluation");
    }

    [Fact]
    public async Task Current_source_change_during_seed_creation_quarantines_the_original_receipt()
    {
        using var transport = await PreviewPublicationTests.PreparedAsync();
        var machine = transport.Scenario.Machine;
        var review = await machine.InvokeAsync("preview", "--configure");
        transport.BeforeSeed = () => transport.Scenario.Source.Source!.Head = new string('e', 40);
        var result = await machine.InvokeAsync("preview", "--configure", "--approve", (string)review.Result["approvalFingerprint"]!);
        Assert.Equal("Unknown", (string?)result.Result["state"]);
        Assert.Equal(1, transport.SeedCreates);
        Assert.Single(transport.SeedDocuments);
        var journal = JsonSerializer.Deserialize<PreviewPublicationJournal>(transport.Values()[FactoryPreviewPublication.RootKey], FactoryGitHubJson.Options)!;
        Assert.Equal(PreviewPublicationPhase.Unknown, journal.Phase);
        Assert.NotNull(journal.Seed);
    }

    [Fact]
    public async Task Metadata_overlay_does_not_accept_changed_or_unrelated_grants()
    {
        using var transport = await PreviewPublicationTests.PreparedAsync();
        var council = transport.Resources.Values.First(r => (string?)r["properties"]?["roleDefinitionId"] == PreviewPreparationTransport.CouncilRole);
        council["properties"]!["createdBy"] = Guid.NewGuid().ToString();
        var refreshed = await transport.Scenario.Machine.InvokeAsync("models", "--verify-only");
        Assert.NotEqual(0, refreshed.Exit);
        Assert.Contains("CurrentReceiptBoundPreviewMetadataUnconfirmed", refreshed.Terminal.Output);
        Assert.Equal(0, transport.SeedCreates);
    }
}
