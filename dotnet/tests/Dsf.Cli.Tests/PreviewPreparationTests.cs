using System.Text.Json.Nodes;
using Dsf.Cli;
using Xunit;

namespace Dsf.Cli.Tests;

public sealed class PreviewPreparationTests
{
    [Fact]
    public async Task Concurrent_approved_operators_share_one_conditional_preparation_and_one_native_dispatch_per_step()
    {
        using var transport = await PreviewPreparationTransport.CreateAsync();
        var machine = transport.Scenario.Machine;
        var plan = await machine.InvokeAsync("preview", "--preview-config", transport.Selection.ToJsonString());
        var fingerprint = (string)plan.Result["approvalFingerprint"]!;
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var callers = 0;
        transport.Scenario.Arm.Product.BeforePut = async (key, create) =>
        {
            if (key != Dsf.Core.Onboarding.FactoryPreviewPreparation.RootKey || !create) return;
            if (Interlocked.Increment(ref callers) == 2) ready.TrySetResult();
            await ready.Task.WaitAsync(TimeSpan.FromMinutes(1));
        };
        var outcomes = await Task.WhenAll(Enumerable.Range(0, 2).Select(_ =>
            machine.InvokeAsync("preview", "--preview-config", transport.Selection.ToJsonString(), "--approve", fingerprint)));
        Assert.Single(outcomes, outcome => outcome.Exit == 0);
        Assert.Single(outcomes, outcome => (string?)outcome.Result["state"] == "Blocked");
        Assert.Equal(plan.Result["plan"]!["steps"]!.AsArray().Count, transport.NativeWrites.Count);
        Assert.Equal(transport.NativeWrites.Count, transport.NativeWrites.Distinct().Count());
    }

    [Fact]
    public async Task Interactive_blank_approval_prints_exact_explicit_plan_without_effects()
    {
        using var transport = await PreviewPreparationTransport.CreateAsync();
        var terminal = new ScriptedTerminal(new(true, true, false), [""]);
        var before = transport.Scenario.Arm.Product.Export();
        var exit = await transport.Scenario.Machine.Boundary.Repository.InvokeCommandAsync(terminal,
            ["onboard", "decide", "preview", "--owner-appconfig-endpoint", "https://owner.azconfig.io", "--product", "widget",
                "--tenant", BoundaryPlanFixture.Tenant, "--subscription", BoundaryPlanFixture.Subscription,
                "--preview-config", transport.Selection.ToJsonString()], CancellationToken.None);
        Assert.Equal(0, exit);
        Assert.Contains("dsf onboard decide preview", terminal.Output);
        Assert.Contains("operatorPermissions", terminal.Output);
        Assert.Contains("\"state\": \"Reviewed\"", terminal.Output);
        Assert.Equal(before, transport.Scenario.Arm.Product.Export());
        Assert.Empty(transport.NativeWrites);
    }

    [Fact]
    public async Task Approved_native_preparation_is_durable_nonready_and_readonly_status_never_redeploys()
    {
        using var transport = await PreviewPreparationTransport.CreateAsync();
        var machine = transport.Scenario.Machine;
        var planned = await machine.InvokeAsync("preview", "--preview-config", transport.Selection.ToJsonString());
        Assert.True(planned.Exit == 0, planned.Terminal.Output);
        var fingerprint = (string)planned.Result["approvalFingerprint"]!;
        var result = await machine.InvokeAsync("preview", "--preview-config", transport.Selection.ToJsonString(), "--approve", fingerprint);
        Assert.True(result.Exit == 0, result.Terminal.Output + result.Terminal.Error);
        Assert.Equal("ConfirmedNotReady", (string?)result.Result["state"]);
        Assert.False((bool)result.Result["currentReady"]!);
        Assert.All(result.Result["effects"]!.AsArray(), e => Assert.Equal("Confirmed", (string?)e!["state"]));
        Assert.NotEmpty(result.Result["receipts"]!.AsArray());
        transport.Scenario.Arm.Product = ReservationTestStore.Restore(transport.Scenario.Arm.Product.Export());
        var start = transport.Requests.Count;
        var state = transport.Scenario.Arm.Product.Export();
        var status = await machine.InvokeAsync("preview", "--status");
        Assert.True(status.Exit == 0, status.Terminal.Output + status.Terminal.Error);
        Assert.Equal("ConfirmedNotReady", (string?)status.Result["state"]);
        Assert.Equal(fingerprint, (string?)status.Result["approvalFingerprint"]);
        Assert.Equal(state, transport.Scenario.Arm.Product.Export());
        Assert.DoesNotContain(transport.Requests.Skip(start), r => r.Method is "PUT" or "DELETE" or "PATCH");
        Assert.Null(transport.Manifest.PreviewOperationId);
        Assert.DoesNotContain(transport.Values().Keys, k => k.StartsWith("dsf/factory/preview/", StringComparison.Ordinal));
        var confirmed = await machine.InvokeAsync("preview", "--resume", "--approve", fingerprint);
        Assert.Equal("ConfirmedNotReady", (string?)confirmed.Result["state"]);
        Assert.Equal(state, transport.Scenario.Arm.Product.Export());
        Assert.DoesNotContain(transport.Requests.Skip(start), r => r.Method is "PUT" or "DELETE" or "PATCH");
    }

    [Fact]
    public async Task Readonly_plan_uses_current_adoption_native_models_and_genuine_telemetry_without_optional_calls()
    {
        using var transport = await PreviewPreparationTransport.CreateAsync();
        var before = transport.Scenario.Arm.Product.Export();
        var start = transport.Requests.Count;
        var result = await transport.Scenario.Machine.InvokeAsync("preview", "--preview-config", transport.Selection.ToJsonString());
        Assert.True(result.Exit == 0, result.Terminal.Output + result.Terminal.Error + "\n" + string.Join("\n", transport.Requests.TakeLast(8)));
        Assert.Equal("Reviewed", (string?)result.Result["state"]);
        Assert.False((bool)result.Result["currentReady"]!);
        Assert.Equal(64, ((string)result.Result["approvalFingerprint"]!).Length);
        Assert.Contains("volunteer", (string?)result.Result["plan"]!["source"]!["charter"]);
        Assert.Equal("azuremonitor-app-content-v1", (string?)result.Result["plan"]!["configuration"]!["telemetry"]!["projectionRevision"]);
        Assert.Null(result.Result["plan"]!["supplementalConfiguration"]);
        Assert.Single(result.Result["plan"]!["steps"]!.AsArray(), s => (string?)s!["action"] == "WorkloadDeployment");
        Assert.Equal(before, transport.Scenario.Arm.Product.Export());
        Assert.DoesNotContain(transport.Requests.Skip(start), r => r.Method is "PUT" or "DELETE" or "PATCH");
        Assert.DoesNotContain(transport.Requests, r => r.Uri.Host == "api.microsoft.ai" || r.Uri.Host.Contains("search.windows.net", StringComparison.Ordinal));
        Assert.Equal(1, transport.TelemetryQueries);
    }

    [Fact]
    public async Task Redirected_preparation_requires_explicit_locators_before_provider_access()
    {
        var terminal = new ScriptedTerminal(new(false, false, false), []);
        var exit = await CliApplication.InvokeAsync(["onboard", "decide", "preview", "--json"],
            CancellationToken.None, terminal);
        Assert.Equal(1, exit);
        var output = JsonNode.Parse(terminal.Output)!;
        Assert.Equal("Blocked", (string?)output["state"]);
        Assert.Contains("--owner-appconfig-endpoint is required", (string?)output["detail"]);
    }
}
