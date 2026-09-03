using Dsf.ControlCenter;

// The Control Center is a separate web process: it resolves the product/config
// authority it governs through at startup and exits non-zero -- naming every
// unset setting -- rather than serving a governance surface it cannot back.
try
{
    var settings = ControlCenterSettings.FromEnvironment();
    var authority = new AppConfigurationProductPolicyAuthority(
        new AzureConfigurationStoreGateway(),
        settings.OwnerAppConfigEndpoint);
    var app = ControlCenterApp.Build(settings, authority);
    await app.RunAsync();
    return 0;
}
catch (ControlCenterConfigurationException exception)
{
    await Console.Error.WriteLineAsync($"dsf-control-center: {exception.Message}");
    return 1;
}
