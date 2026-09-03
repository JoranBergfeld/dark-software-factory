using Dsf.ControlCenter;
using Xunit;

namespace Dsf.ControlCenter.Tests;

/// <summary>
/// The Control Center is a governance write surface: it must refuse to start
/// without the owner App Configuration authority it governs through and without
/// the operator credential that protects every write.
/// </summary>
public sealed class ControlCenterSettingsTests
{
    private static Dictionary<string, string?> Complete() => new()
    {
        [ControlCenterSettings.OwnerAppConfigEndpointEnv] = "https://owner.azconfig.io",
        [ControlCenterSettings.OperatorTokenEnv] = "operator-secret",
    };

    [Fact]
    public void Composes_from_a_complete_environment()
    {
        var settings = ControlCenterSettings.FromEnvironment(Complete());

        Assert.Equal("https://owner.azconfig.io", settings.OwnerAppConfigEndpoint);
        Assert.Equal("operator-secret", settings.OperatorToken);
        Assert.Equal("127.0.0.1", settings.Host);
        Assert.Equal(8081, settings.Port);
        Assert.True(settings.RequireSecureCookies);
    }

    [Fact]
    public void Missing_configuration_names_every_unset_requirement()
    {
        var exception = Assert.Throws<ControlCenterConfigurationException>(
            () => ControlCenterSettings.FromEnvironment(new Dictionary<string, string?>()));

        Assert.Contains(ControlCenterSettings.OwnerAppConfigEndpointEnv, exception.MissingSettings);
        Assert.Contains(ControlCenterSettings.OperatorTokenEnv, exception.MissingSettings);
        Assert.Contains(ControlCenterSettings.OwnerAppConfigEndpointEnv, exception.Message, StringComparison.Ordinal);
        Assert.Contains(ControlCenterSettings.OperatorTokenEnv, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Blank_operator_token_fails_closed()
    {
        var env = Complete();
        env[ControlCenterSettings.OperatorTokenEnv] = "   ";

        var exception = Assert.Throws<ControlCenterConfigurationException>(
            () => ControlCenterSettings.FromEnvironment(env));

        Assert.Equal([ControlCenterSettings.OperatorTokenEnv], exception.MissingSettings);
    }

    [Fact]
    public void Host_and_port_and_cookie_policy_are_overridable()
    {
        var env = Complete();
        env[ControlCenterSettings.HostEnv] = "0.0.0.0";
        env[ControlCenterSettings.PortEnv] = "9090";
        env[ControlCenterSettings.SecureCookiesEnv] = "false";

        var settings = ControlCenterSettings.FromEnvironment(env);

        Assert.Equal("0.0.0.0", settings.Host);
        Assert.Equal(9090, settings.Port);
        Assert.False(settings.RequireSecureCookies);
    }

    [Fact]
    public void Nonnumeric_port_fails_loudly()
    {
        var env = Complete();
        env[ControlCenterSettings.PortEnv] = "http";

        var exception = Assert.Throws<ControlCenterConfigurationException>(
            () => ControlCenterSettings.FromEnvironment(env));

        Assert.Equal([ControlCenterSettings.PortEnv], exception.MissingSettings);
    }
}
