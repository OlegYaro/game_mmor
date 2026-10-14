namespace Core.Tests;

public sealed class CoreInfoTests
{
    [Fact]
    [Trait("Req", "NFR-09")]
    public void VersionMatchesCoreAssemblyVersion()
    {
        Version? assemblyVersion = typeof(CoreInfo).Assembly.GetName().Version;

        Assert.NotNull(assemblyVersion);
        Assert.Equal(assemblyVersion, CoreInfo.Version);
        Assert.True(CoreInfo.Version > new Version(0, 0), "Core version must be set in Directory.Build.props.");
        Assert.Equal("Core", CoreInfo.Name);
    }
}
