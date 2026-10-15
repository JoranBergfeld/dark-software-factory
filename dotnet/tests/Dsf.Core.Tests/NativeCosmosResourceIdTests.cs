using System.Text.Json;
using Dsf.Core.Runtime;
using Xunit;

namespace Dsf.Core.Tests;

public sealed class NativeCosmosResourceIdTests
{
    [Theory]
    [InlineData("-wAAAIAAAAABAAAAAAAAAA==")]
    [InlineData("/wAAAIAAAAABAAAAAAAAAA==")]
    [InlineData("PD5DALigD00BAAAAAAAAAA==")]
    public void Native_document_identity_is_retained_exactly(string rid) =>
        Assert.Equal(rid, NativeCosmosResourceId.RequireDocument(rid));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("-wAAAA==")]
    [InlineData("-wAAAIAAAAA=")]
    [InlineData("-wAAAIAAAAABAAAAAAAAAAAAAAA=")]
    [InlineData("_wAAAIAAAAABAAAAAAAAAA==")]
    [InlineData("-wAAAIAAAAABAAAAAAAAAA")]
    [InlineData("-wAAAIAAAAABAAAAAAAAAB==")]
    [InlineData("-wAAAIAAAAABAAAAAAAAAA= ")]
    [InlineData("-wAAAIAAAAABAAAAAAAAEA==")]
    [InlineData("-wAAAAEAAAABAAAAAAAAAA==")]
    [InlineData("AAAAAIAAAAABAAAAAAAAAA==")]
    [InlineData("-wAAAIAAAAAAAAAAAAAAAA==")]
    public void Malformed_noncanonical_or_wrong_resource_shapes_are_rejected(string? rid) =>
        Assert.Throws<JsonException>(() => NativeCosmosResourceId.RequireDocument(rid));
}
