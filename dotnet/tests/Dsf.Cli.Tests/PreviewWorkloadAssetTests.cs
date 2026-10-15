using System.Text.Json;
using Dsf.Cli;
using Dsf.Core.Onboarding;
using Xunit;

namespace Dsf.Cli.Tests;

public sealed class PreviewWorkloadAssetTests
{
    [Fact]
    public void Compiled_asset_reuses_original_telemetry_identity_not_a_host_named_identity()
    {
        using var asset = Read();
        var resources = asset.RootElement.GetProperty("resources");
        Assert.True(resources.GetProperty("telemetryIdentity").GetProperty("existing").GetBoolean());
        Assert.Equal("[format('{0}-source', parameters('namePrefix'))]",
            resources.GetProperty("telemetryIdentity").GetProperty("name").GetString());
        Assert.Equal("[format('{0}-code', parameters('namePrefix'))]",
            resources.GetProperty("codeIdentity").GetProperty("name").GetString());
        Assert.Equal("[format('{0}-council', parameters('namePrefix'))]",
            resources.GetProperty("councilIdentity").GetProperty("name").GetString());
        Assert.All(resources.EnumerateObject().Where(p => p.Value.GetProperty("type").GetString()
            is "Microsoft.ManagedIdentity/userAssignedIdentities" or "Microsoft.App/managedEnvironments"
                or "Microsoft.DocumentDB/databaseAccounts" or "Microsoft.DocumentDB/databaseAccounts/sqlDatabases"),
            p => Assert.True(p.Value.GetProperty("existing").GetBoolean()));
    }

    [Fact]
    public void Compiled_state_is_product_partitioned_without_query_delete_or_broad_item_grants()
    {
        using var asset = Read();
        var resources = asset.RootElement.GetProperty("resources");
        var container = resources.GetProperty("previewContainer").GetProperty("properties").GetProperty("resource");
        Assert.Equal("council-preview", container.GetProperty("id").GetString());
        var partition = container.GetProperty("partitionKey");
        Assert.Equal(new[] { "/product" }, partition.GetProperty("paths").EnumerateArray().Select(e => e.GetString()));
        Assert.Equal("Hash", partition.GetProperty("kind").GetString());
        Assert.Equal(2, partition.GetProperty("version").GetInt32());
        var role = resources.GetProperty("stateRole").GetProperty("properties");
        Assert.Equal("CustomRole", role.GetProperty("type").GetString());
        Assert.Equal(new[]
        {
            "Microsoft.DocumentDB/databaseAccounts/readMetadata",
            "Microsoft.DocumentDB/databaseAccounts/sqlDatabases/containers/items/read",
            "Microsoft.DocumentDB/databaseAccounts/sqlDatabases/containers/items/create",
            "Microsoft.DocumentDB/databaseAccounts/sqlDatabases/containers/items/replace",
        }, role.GetProperty("permissions")[0].GetProperty("dataActions").EnumerateArray().Select(e => e.GetString()));
        Assert.Single(role.GetProperty("assignableScopes").EnumerateArray());
        Assert.Equal(2, resources.EnumerateObject().Count(p => p.Value.GetProperty("type").GetString()
            == "Microsoft.DocumentDB/databaseAccounts/sqlRoleAssignments"));
        foreach (var name in new[] { "runsAssignment", "previewAssignment" })
        {
            var grant = resources.GetProperty(name).GetProperty("properties");
            Assert.Equal("[reference('councilIdentity').principalId]", grant.GetProperty("principalId").GetString());
            Assert.Contains("/dbs/{1}/colls/", grant.GetProperty("scope").GetString());
        }
        Assert.DoesNotContain("guid(", resources.GetProperty("stateRole").GetProperty("name").GetString());
    }

    [Fact]
    public void Compiled_supplemental_resources_are_selection_bounded_and_use_actual_coordinator_output()
    {
        using var asset = Read();
        var root = asset.RootElement;
        var selection = root.GetProperty("parameters").GetProperty("supplementalSources");
        Assert.Empty(selection.GetProperty("defaultValue").EnumerateArray());
        Assert.Equal(2, selection.GetProperty("maxLength").GetInt32());
        Assert.Equal(new[] { "foundryiq", "webiq" }, root.GetProperty("definitions").GetProperty("supplementalSource")
            .GetProperty("properties").GetProperty("kind").GetProperty("allowedValues").EnumerateArray().Select(e => e.GetString()));
        Assert.False(root.GetProperty("definitions").GetProperty("supplementalSource").GetProperty("additionalProperties").GetBoolean());
        var resources = root.GetProperty("resources");
        foreach (var name in new[] { "supplementalIdentities", "supplementalHosts", "supplementalAuthentication" })
            Assert.Equal("[length(parameters('supplementalSources'))]", resources.GetProperty(name).GetProperty("copy").GetProperty("count").GetString());
        var host = resources.GetProperty("supplementalHosts");
        Assert.Contains("councilHost", host.GetProperty("dependsOn").EnumerateArray().Select(e => e.GetString()));
        var environment = host.GetProperty("properties").GetProperty("template").GetProperty("containers")[0].GetProperty("env");
        var coordinator = environment.EnumerateArray().Single(e => e.GetProperty("name").GetString() == "DSF_PREVIEW_COORDINATOR")
            .GetProperty("value").GetString();
        Assert.Contains("reference('councilHost').configuration.ingress.fqdn", coordinator);
        Assert.DoesNotContain("private.azurecontainerapps.io", coordinator);
        Assert.Single(root.GetProperty("outputs").EnumerateObject());
    }

    [Theory]
    [InlineData("consumerAuthentication")]
    [InlineData("supplementalAuthentication")]
    public void Compiled_consumer_auth_requires_council_principal_and_client(string name)
    {
        using var asset = Read();
        var properties = asset.RootElement.GetProperty("resources").GetProperty(name).GetProperty("properties");
        Assert.True(properties.GetProperty("platform").GetProperty("enabled").GetBoolean());
        Assert.Equal("Return401", properties.GetProperty("globalValidation").GetProperty("unauthenticatedClientAction").GetString());
        Assert.Empty(properties.GetProperty("globalValidation").GetProperty("excludedPaths").EnumerateArray());
        Assert.True(properties.GetProperty("httpSettings").GetProperty("requireHttps").GetBoolean());
        Assert.False(properties.GetProperty("login").GetProperty("tokenStore").GetProperty("enabled").GetBoolean());
        var policy = properties.GetProperty("identityProviders").GetProperty("azureActiveDirectory")
            .GetProperty("validation").GetProperty("defaultAuthorizationPolicy");
        Assert.Equal(new[] { "[reference('councilIdentity').clientId]" },
            policy.GetProperty("allowedApplications").EnumerateArray().Select(e => e.GetString()));
        Assert.Equal(new[] { "[reference('councilIdentity').principalId]" },
            policy.GetProperty("allowedPrincipals").GetProperty("identities").EnumerateArray().Select(e => e.GetString()));
    }

    [Fact]
    public void Compiled_coordinator_setting_has_exact_runtime_serialization_without_object_order_assumptions()
    {
        using var asset = Read();
        var expression = asset.RootElement.GetProperty("resources").GetProperty("supplementalHosts").GetProperty("properties")
            .GetProperty("template").GetProperty("containers")[0].GetProperty("env").EnumerateArray()
            .Single(e => e.GetProperty("name").GetString() == "DSF_PREVIEW_COORDINATOR").GetProperty("value").GetString()!;
        Assert.StartsWith("[format('", expression);
        var end = expression.IndexOf("', ", StringComparison.Ordinal);
        var format = expression["[format('".Length..end];
        Assert.Equal("reference('councilHost').configuration.ingress.fqdn, parameters('audienceClientId'), reference('councilIdentity').principalId, reference('councilIdentity').clientId)]",
            expression[(end + 3)..]);
        var coordinator = new PreviewSupplementalCoordinator("https://actual-native.azurecontainerapps.io",
            "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa", "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb", "cccccccc-cccc-cccc-cccc-cccccccccccc");
        Assert.Equal(JsonSerializer.Serialize(coordinator, FactoryGitHubJson.Options),
            string.Format(System.Globalization.CultureInfo.InvariantCulture, format, "actual-native.azurecontainerapps.io",
                coordinator.AudienceClientId, coordinator.PrincipalId, coordinator.ClientId));
    }

    private static JsonDocument Read() => JsonDocument.Parse(File.ReadAllText(
        new ProvisioningAssets().Resolve("onboard-preview-workloads.json")));
}
