using Dsf.Core.Onboarding;
using Xunit;

namespace Dsf.Core.Tests;

public sealed class FactorySourceEvidenceIntegrityTests
{
    [Theory]
    [InlineData("""{"ClientSecret":"ordinary-client-secret-value"}""")]
    [InlineData("""{"apiKey":"ordinary-api-key-value"}""")]
    [InlineData("AccessToken = \"ordinary-access-token-value\"")]
    public void Common_dotnet_credential_setting_names_are_sensitive(string content)
    {
        Assert.True(FactorySourceEvidenceIntegrity.Sensitive(content));
    }
}
