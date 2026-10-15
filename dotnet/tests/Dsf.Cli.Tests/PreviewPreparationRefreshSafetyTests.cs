using System.Text.Json.Nodes;
using Dsf.Core.Onboarding;
using Dsf.Core.Runtime;
using Xunit;

namespace Dsf.Cli.Tests;

public sealed class PreviewPreparationRefreshSafetyTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Expired_selected_proof_before_approval_creates_no_preparation_or_native_effect(bool foundry)
    {
        using var transport = await PreviewPreparationTransport.CreateAsync();
        if (foundry) await transport.SelectFoundryAsync();
        else await transport.SelectWebAsync();
        var machine = transport.Scenario.Machine;
        var plan = await machine.InvokeAsync("preview", "--preview-config", transport.Selection.ToJsonString());
        Assert.True(plan.Exit == 0, plan.Terminal.Output);
        var original = transport.Scenario.Arm.Product.Export();
        transport.Scenario.Source.Source!.Clock.Now += TimeSpan.FromSeconds(121);
        var refused = await machine.InvokeAsync("preview", "--preview-config", transport.Selection.ToJsonString(),
            "--approve", (string)plan.Result["approvalFingerprint"]!);
        Assert.NotEqual(0, refused.Exit);
        Assert.False((bool)refused.Result["currentReady"]!);
        Assert.Empty(transport.NativeWrites);
        Assert.Equal(original, transport.Scenario.Arm.Product.Export());
    }

    [Theory]
    [InlineData("none")]
    [InlineData("grant")]
    [InlineData("incarnation")]
    [InlineData("unknown")]
    [InlineData("configuration")]
    public async Task Partial_refresh_recognizes_only_exact_confirmed_metadata_additions(string changed)
    {
        using var transport = await PreviewPreparationTransport.CreateAsync();
        var machine = transport.Scenario.Machine;
        var plan = await machine.InvokeAsync("preview", "--preview-config", transport.Selection.ToJsonString());
        var approval = (string)plan.Result["approvalFingerprint"]!;
        transport.AfterNativeWrite = _ =>
        {
            if (transport.NativeWrites.Count == 2)
            {
                transport.Scenario.Source.Source!.Clock.Now += TimeSpan.FromSeconds(121);
                transport.DeniedAction = "Microsoft.Authorization/roleAssignments/write";
            }
        };
        var partial = await machine.InvokeAsync("preview", "--preview-config", transport.Selection.ToJsonString(), "--approve", approval);
        Assert.Equal("Blocked", (string?)partial.Result["state"]);
        Assert.Equal(2, transport.NativeWrites.Count);
        Assert.All(partial.Result["effects"]!.AsArray().Take(2), e => Assert.Equal("Confirmed", (string?)e!["state"]));
        var originalReceipts = partial.Result["receipts"]!.DeepClone();
        var properties = transport.Resources[transport.NativeWrites[1]]["properties"]!;
        if (changed == "grant") properties["condition"] = "unreviewed-condition";
        if (changed == "incarnation") properties["createdOn"] = properties["createdOn"]!.GetValue<DateTimeOffset>().AddSeconds(1);
        if (changed == "configuration") transport.Scenario.Arm.Product.Change(ProductConfigurationKeys.FactoryModels, raw =>
        {
            var value = JsonNode.Parse(raw)!;
            value["configuration"]!["runtimeImage"] = "unreviewed-image";
            return value.ToJsonString();
        });
        if (changed == "unknown") transport.Scenario.Arm.Product.Change(FactoryPreviewPreparation.RootKey, raw =>
        {
            var value = JsonNode.Parse(raw)!;
            value["state"] = "Unknown";
            return value.ToJsonString();
        });
        transport.Restart();
        transport.DeniedAction = null;
        var before = transport.Requests.Count;
        var refreshed = await machine.InvokeAsync("models", "--verify-only");
        Assert.Equal(changed == "none" ? 0 : 1, refreshed.Exit);
        if (changed != "none")
            Assert.DoesNotContain(transport.Requests.Skip(before), r => r.Uri.Host == "shared-models.services.ai.azure.com");
        var resumed = await machine.InvokeAsync("preview", "--resume", "--approve", approval);
        if (changed == "none")
        {
            Assert.True(resumed.Exit == 0, resumed.Terminal.Output);
            Assert.Equal(approval, (string?)resumed.Result["approvalFingerprint"]);
            foreach (var receipt in originalReceipts.AsArray())
                Assert.Contains(resumed.Result["receipts"]!.AsArray(), r => JsonNode.DeepEquals(r, receipt));
            Assert.Equal(1, transport.NativeWrites.Count(t => t == (string)plan.Result["plan"]!["steps"]![0]!["target"]!));
        }
        else
        {
            Assert.NotEqual(0, resumed.Exit);
            Assert.Equal(2, transport.NativeWrites.Count);
        }
        Assert.Null(transport.Manifest.PreviewOperationId);
        Assert.Empty(transport.SeedDocuments);
        Assert.False((bool)resumed.Result["currentReady"]!);
    }

    [Fact]
    public async Task Refresh_does_not_allow_erasing_the_original_reviewed_proof()
    {
        using var transport = await PreviewPublicationTests.PreparedAsync(web: true);
        var machine = transport.Scenario.Machine;
        var originalProof = transport.Values().Keys.Single(k => k.StartsWith("dsf/factory/supplemental-proofs/", StringComparison.Ordinal));
        transport.Scenario.Source.Source!.Clock.Now += TimeSpan.FromSeconds(121);
        Assert.Equal(0, (await machine.InvokeAsync("supplemental", "--verify-only")).Exit);
        transport.Scenario.Arm.Product.Remove(originalProof);
        var before = transport.NativeWrites.Count;
        var configured = await machine.InvokeAsync("preview", "--configure");
        Assert.NotEqual(0, configured.Exit);
        Assert.False((bool)configured.Result["currentReady"]!);
        Assert.Equal(before, transport.NativeWrites.Count);
        Assert.Null(transport.Manifest.PreviewOperationId);
        Assert.Empty(transport.SeedDocuments);
    }
}
