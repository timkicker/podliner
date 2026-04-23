namespace StuiPodcast.Core.Sync;

// gPodder-compatible protocol flavors.
// - GpodderNet: classic gpodder.net / mygpo / opodsync API v2, /api/2/ prefix.
// - Nextcloud: thrillfall/nextcloud-gpodder app under /index.php/apps/gpoddersync/.
// The config stores a flavor per configured server so subsequent syncs
// skip the login-time probe; `Auto` means "not yet detected".
public enum GpodderFlavor
{
    Auto = 0,
    GpodderNet,
    Nextcloud
}

public static class GpodderFlavorExt
{
    public static string ToWire(this GpodderFlavor f) => f switch
    {
        GpodderFlavor.GpodderNet => "gpoddernet",
        GpodderFlavor.Nextcloud  => "nextcloud",
        _                        => "auto"
    };

    public static GpodderFlavor FromWire(string? raw)
    {
        var s = (raw ?? "").Trim().ToLowerInvariant();
        return s switch
        {
            "gpoddernet" or "gpodder" or "gpodder.net" or "mygpo" => GpodderFlavor.GpodderNet,
            "nextcloud" or "nc"                                   => GpodderFlavor.Nextcloud,
            _                                                     => GpodderFlavor.Auto
        };
    }
}
