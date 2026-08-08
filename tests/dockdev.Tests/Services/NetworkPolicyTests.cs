using dockdev.Models;
using dockdev.Services;
using Xunit;

namespace dockdev.Tests.Services;

/// <summary>Design doc §21: a behavioural test asserting <see cref="NetworkPolicy"/> denies every
/// capability with consent withheld.</summary>
public class NetworkPolicyTests
{
    [Fact]
    public void FreshConfig_DeniesEveryCapability()
    {
        var config = new DockConfig();
        Assert.True(NetworkPolicy.AllCapabilitiesDenied(config));
        Assert.False(NetworkPolicy.UpdateCheckAllowed(config));
        Assert.Null(NetworkPolicy.CreateClientForUpdateCheck(config));
    }

    [Fact]
    public void UpdateCheck_OnlyAllowedWhenExplicitlyEnabled()
    {
        var config = new DockConfig { Network = new NetworkSettings { UpdateCheck = true } };
        Assert.True(NetworkPolicy.UpdateCheckAllowed(config));
        Assert.False(NetworkPolicy.AllCapabilitiesDenied(config));
        using var client = NetworkPolicy.CreateClientForUpdateCheck(config);
        Assert.NotNull(client);
    }
}
