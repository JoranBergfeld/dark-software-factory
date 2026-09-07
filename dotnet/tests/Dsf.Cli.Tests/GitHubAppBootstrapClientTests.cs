using Dsf.Cli;
using Xunit;

namespace Dsf.Cli.Tests;

public sealed class GitHubAppBootstrapClientTests
{
    [Fact]
    public void Manifest_has_only_required_permissions_and_no_webhook()
    {
        var manifest = GitHubAppManifest.Create(
            "dsf-sbx-20260907",
            new Uri("http://127.0.0.1:8765/callback"));

        Assert.Equal("write", manifest.DefaultPermissions["issues"]);
        Assert.Equal("write", manifest.DefaultPermissions["pull_requests"]);
        Assert.Equal("write", manifest.DefaultPermissions["contents"]);
        Assert.Equal("write", manifest.DefaultPermissions["administration"]);
        Assert.Empty(manifest.DefaultEvents);
        Assert.False(manifest.Public);
    }

    [Theory]
    [InlineData("abc123", "abc123")]
    [InlineData("?code=abc123", "abc123")]
    [InlineData("http://127.0.0.1:8765/callback?code=abc123&state=x", "abc123")]
    public void Callback_code_parser_accepts_interactive_and_headless_forms(string raw, string expected)
    {
        Assert.Equal(expected, GitHubAppManifest.ParseCode(raw));
    }
}
