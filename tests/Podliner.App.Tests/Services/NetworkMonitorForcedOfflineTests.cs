using FluentAssertions;
using Podliner.App.Services;
using Podliner.App.Tests.Fakes;
using Xunit;

namespace Podliner.App.Tests.Services;

// The monitor's first probe used to write its result straight into
// NetworkOnline, a moment after --offline had set it to false, so --offline
// never held. Measured on 2.0.2: start with --offline, :refresh six seconds
// later, and the feed server got a request.
public sealed class NetworkMonitorForcedOfflineTests : IDisposable
{
    private readonly AppServicesBuilder _b = new();
    public void Dispose() => _b.Dispose();

    [Fact]
    public void A_probe_that_finds_the_network_does_not_undo_a_chosen_offline()
    {
        _b.Data.ForcedOffline = true;
        _b.Data.NetworkOnline = false;

        _b.Net.OnNetworkChanged(online: true);

        _b.Data.NetworkOnline.Should().BeFalse();
    }

    [Fact]
    public void Without_a_choice_the_probe_still_decides()
    {
        _b.Data.ForcedOffline = false;
        _b.Data.NetworkOnline = false;

        _b.Net.OnNetworkChanged(online: true);

        _b.Data.NetworkOnline.Should().BeTrue();
    }

    [Fact]
    public void A_probe_that_loses_the_network_is_always_believed()
    {
        _b.Data.ForcedOffline = false;
        _b.Data.NetworkOnline = true;

        _b.Net.OnNetworkChanged(online: false);

        _b.Data.NetworkOnline.Should().BeFalse();
    }
}
