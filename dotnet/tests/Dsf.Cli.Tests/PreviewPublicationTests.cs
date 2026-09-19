using System.Text.Json.Nodes;
using Dsf.Core.Onboarding;
using Dsf.Core.Runtime;
using Xunit;

namespace Dsf.Cli.Tests;

public sealed class PreviewPublicationTests
{
    [Fact]
    public async Task Receipt_bound_metadata_allows_real_predecessor_proof_refresh_without_repinning_preparation()
    {
        using var transport = await PreparedAsync();
        var machine = transport.Scenario.Machine;
        var before = await machine.InvokeAsync("preview", "--configure");
        var refreshed = await machine.InvokeAsync("models", "--verify-only");
        Assert.True(refreshed.Exit == 0, (string?)refreshed.Result["detail"] + refreshed.Terminal.Error);
        var telemetry = await machine.InvokeAsync("telemetry", "--verify-only");
        Assert.True(telemetry.Exit == 0, (string?)telemetry.Result["detail"]);
        var after = await machine.InvokeAsync("preview", "--configure");
        Assert.True(after.Exit == 0, (string?)after.Result["detail"]);
        Assert.Equal((string?)before.Result["approvalFingerprint"], (string?)after.Result["approvalFingerprint"]);
    }

    [Fact]
    public async Task Configuration_continuation_requires_its_exact_review_and_does_not_redeploy_preparation()
    {
        using var transport = await PreparedAsync();
        var machine = transport.Scenario.Machine;
        var before = transport.Scenario.Arm.Product.Export();
        var writes = transport.NativeWrites.Count;
        var requestStart = transport.Requests.Count;
        var sourceWrites = transport.Scenario.Source.Source!.Writes.Count;
        var manifestBasis = FactoryPreviewValidation.ManifestBasis(transport.Manifest);
        var review = await machine.InvokeAsync("preview", "--configure");
        Assert.True(review.Exit == 0, review.Terminal.Output + review.Terminal.Error);
        Assert.Equal("Reviewed", (string?)review.Result["state"]);
        Assert.NotNull(review.Result["publication"]);
        Assert.Equal(64, ((string)review.Result["approvalFingerprint"]!).Length);
        Assert.Equal(before, transport.Scenario.Arm.Product.Export());
        Assert.Equal(writes, transport.NativeWrites.Count);
        var wrong = await machine.InvokeAsync("preview", "--configure", "--approve", new string('a', 64));
        Assert.Equal("Blocked", (string?)wrong.Result["state"]);
        Assert.Equal(before, transport.Scenario.Arm.Product.Export());
        var seeded = await machine.InvokeAsync("preview", "--configure", "--approve", (string)review.Result["approvalFingerprint"]!);
        Assert.True(seeded.Exit == 0, (string?)seeded.Result["detail"] + "; " + transport.LastPreviewResponse
            + "\n" + string.Join("\n", transport.Requests.TakeLast(8)));
        Assert.Equal("SeedAwaitingApproval", (string?)seeded.Result["state"]);
        Assert.NotNull(seeded.Result["publication"]!["execution"]!["seed"]);
        var persisted = JsonNode.Parse(transport.SeedDocuments.Values.Single())!.AsObject();
        persisted.Remove("_rid");
        persisted.Remove("_etag");
        Assert.True(JsonNode.DeepEquals(persisted, JsonNode.Parse((string)review.Result["publication"]!["seedIntent"]!["document"]!)));
        var configured = await machine.InvokeAsync("preview", "--configure", "--approve", (string)seeded.Result["approvalFingerprint"]!);
        Assert.True(configured.Exit == 0, (string?)configured.Result["detail"] + "; " + transport.LastPreviewResponse);
        Assert.Equal("ConfiguredAwaitingEvaluation", (string?)configured.Result["state"]);
        Assert.False((bool)configured.Result["currentReady"]!);
        Assert.Equal(writes, transport.NativeWrites.Count);
        Assert.Equal(manifestBasis, FactoryPreviewValidation.ManifestBasis(transport.Manifest));
        Assert.Equal(sourceWrites, transport.Scenario.Source.Source.Writes.Count);
        Assert.DoesNotContain(transport.Requests.Skip(requestStart), r => r.Uri.Host == "shared-models.services.ai.azure.com"
            || r.Method is "PATCH" or "DELETE" || r.Method == "PUT" && (r.Uri.Host == "management.azure.com"
                || !(Uri.UnescapeDataString(r.Uri.AbsolutePath).StartsWith("/kv/dsf/factory/preview", StringComparison.Ordinal)
                    || Uri.UnescapeDataString(r.Uri.AbsolutePath) == "/kv/" + ProductConfigurationKeys.FactoryManifest)));
        var approvals = transport.Values().Where(p => p.Key.StartsWith(FactoryPreviewPublication.Namespace, StringComparison.Ordinal)
            && p.Key.EndsWith("/approval", StringComparison.Ordinal)).Select(p => JsonNode.Parse(p.Value)!).ToArray();
        Assert.Equal(2, approvals.Length);
        Assert.Contains(approvals, a => (string?)a["approvalFingerprint"] == (string?)review.Result["approvalFingerprint"]);
        Assert.Contains(approvals, a => (string?)a["approvalFingerprint"] == (string?)seeded.Result["approvalFingerprint"]);
        transport.Scenario.Arm.Product = ReservationTestStore.Restore(transport.Scenario.Arm.Product.Export());
        var retained = transport.Scenario.Arm.Product.Export();
        var status = await machine.InvokeAsync("preview", "--configure", "--status");
        Assert.True(status.Exit == 0, status.Terminal.Output + status.Terminal.Error);
        Assert.Equal("ConfiguredAwaitingEvaluation", (string?)status.Result["state"]);
        Assert.Equal(retained, transport.Scenario.Arm.Product.Export());
    }

    internal static async Task<PreviewPreparationTransport> PreparedAsync(bool web = false, bool foundry = false, bool publisher = false)
    {
        var transport = await PreviewPreparationTransport.CreateAsync(publisher);
        if (web) await transport.SelectWebAsync();
        if (foundry) await transport.SelectFoundryAsync();
        var machine = transport.Scenario.Machine;
        var plan = await machine.InvokeAsync("preview", "--preview-config", transport.Selection.ToJsonString());
        Assert.True(plan.Exit == 0, plan.Terminal.Output + plan.Terminal.Error);
        var applied = await machine.InvokeAsync("preview", "--preview-config", transport.Selection.ToJsonString(),
            "--approve", (string)plan.Result["approvalFingerprint"]!);
        Assert.True(applied.Exit == 0, (string?)applied.Result["detail"] + "\n" + string.Join("\n", transport.Requests.TakeLast(12)));
        return transport;
    }
}
