using Dsf.Core;
using Dsf.Testing;
using Xunit;

namespace Dsf.Core.Tests;

public sealed class CoreModuleTests
{
    [Fact]
    public void Name_identifies_the_core_module()
    {
        Assert.Equal("Dsf.Core", CoreModule.Name);
    }

    [Fact]
    public void FakeClock_default_is_deterministic()
    {
        var clock = new FakeClock();

        Assert.Equal(DateTimeOffset.UnixEpoch, clock.UtcNow);
    }
}
