using System.Globalization;

namespace Dsf.ControlCenter;

/// <summary>
/// Raised when the Control Center cannot resolve the configuration it needs to
/// govern anything. It names every unset requirement so an operator fixes the
/// deployment in one pass instead of one variable per restart.
/// </summary>
internal sealed class ControlCenterConfigurationException(string message, IReadOnlyList<string> missingSettings)
    : Exception(message)
{
    public IReadOnlyList<string> MissingSettings { get; } = missingSettings;
}

/// <summary>
/// Process configuration for the Control Center web process. The owner App
/// Configuration endpoint is the product/config authority the UI governs through
/// (the same store <c>dsf new</c> publishes its runtime index into), and the
/// operator token is the credential that gates every write. Both are required:
/// there is no open/local mode, so a misconfigured deployment fails at startup
/// rather than serving an unprotected write surface.
/// </summary>
internal sealed record ControlCenterSettings(
    string OwnerAppConfigEndpoint,
    string OperatorToken,
    string Host,
    int Port,
    bool RequireSecureCookies)
{
    public const string OwnerAppConfigEndpointEnv = "DSF_OWNER_APPCONFIG_ENDPOINT";
    public const string OperatorTokenEnv = "DSF_CONTROL_CENTER_TOKEN";
    public const string HostEnv = "DSF_CONTROL_CENTER_HOST";
    public const string PortEnv = "DSF_CONTROL_CENTER_PORT";
    public const string SecureCookiesEnv = "DSF_CONTROL_CENTER_SECURE_COOKIES";

    public const string DefaultHost = "127.0.0.1";
    public const int DefaultPort = 8081;

    public static ControlCenterSettings FromEnvironment(IReadOnlyDictionary<string, string?> env)
    {
        ArgumentNullException.ThrowIfNull(env);

        string Read(string name) => (env.TryGetValue(name, out var value) ? value : null)?.Trim() ?? string.Empty;

        var owner = Read(OwnerAppConfigEndpointEnv);
        var token = Read(OperatorTokenEnv);

        var missing = new List<string>();
        if (owner.Length == 0)
        {
            missing.Add(OwnerAppConfigEndpointEnv);
        }

        if (token.Length == 0)
        {
            missing.Add(OperatorTokenEnv);
        }

        if (missing.Count > 0)
        {
            throw new ControlCenterConfigurationException(
                "missing required Control Center configuration: " + string.Join(", ", missing),
                missing);
        }

        var host = Read(HostEnv);
        var rawPort = Read(PortEnv);
        var port = DefaultPort;
        if (rawPort.Length > 0
            && !int.TryParse(rawPort, NumberStyles.Integer, CultureInfo.InvariantCulture, out port))
        {
            throw new ControlCenterConfigurationException(
                $"{PortEnv} must be a port number, not '{rawPort}'.",
                [PortEnv]);
        }

        var rawSecure = Read(SecureCookiesEnv);
        var secure = rawSecure.Length == 0
            || !string.Equals(rawSecure, "false", StringComparison.OrdinalIgnoreCase);

        return new ControlCenterSettings(
            owner,
            token,
            host.Length == 0 ? DefaultHost : host,
            port,
            secure);
    }

    public static ControlCenterSettings FromEnvironment(
        IReadOnlyDictionary<string, string?> env,
        IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(env);
        ArgumentNullException.ThrowIfNull(args);

        var overlay = new Dictionary<string, string?>(env, StringComparer.Ordinal);
        for (var index = 0; index < args.Count; index++)
        {
            var name = args[index];
            if (name is not ("--host" or "--port"))
            {
                throw new ControlCenterConfigurationException(
                    $"unknown Control Center option '{name}'.",
                    [name]);
            }

            if (index + 1 >= args.Count)
            {
                throw new ControlCenterConfigurationException(
                    $"{name} requires a value.",
                    [name]);
            }

            overlay[name == "--host" ? HostEnv : PortEnv] = args[++index];
        }

        return FromEnvironment(overlay);
    }

    /// <summary>Resolves settings from the real process environment.</summary>
    public static ControlCenterSettings FromEnvironment(IReadOnlyList<string> args)
    {
        var env = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (System.Collections.DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            env[(string)entry.Key] = entry.Value as string;
        }

        return FromEnvironment(env, args);
    }
}
