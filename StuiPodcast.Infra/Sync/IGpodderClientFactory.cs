using StuiPodcast.Core.Sync;

namespace StuiPodcast.Infra.Sync;

// Creates the right IGpodderClient for a given flavor. Lets the sync
// service switch protocols after auto-detect without knowing the concrete
// types. Injected in tests with a hand-rolled fake; production uses
// GpodderClientFactory (default).
public interface IGpodderClientFactory
{
    IGpodderClient Create(GpodderFlavor flavor);
}

public sealed class GpodderClientFactory : IGpodderClientFactory
{
    public IGpodderClient Create(GpodderFlavor flavor) => flavor switch
    {
        GpodderFlavor.Nextcloud => new NextcloudGpodderClient(),
        _                       => new GpodderNetClient(), // Auto + GpodderNet default to the gpodder.net protocol
    };
}
