namespace StuiPodcast.Core.Sync;

public sealed class GpodderSyncConfig
{
    public string?  ServerUrl  { get; set; }
    public string?  Username   { get; set; }
    public string?  Password   { get; set; }    // plain-text local storage
    public string   DeviceId   { get; set; } = "";
    public bool     AutoSync   { get; set; } = false;
    public long     SubsTimestamp    { get; set; } = 0;   // use server's timestamp, not local clock
    public long     ActionsTimestamp { get; set; } = 0;
    public List<string>               LastKnownServerFeeds { get; set; } = new();
    public List<PendingGpodderAction> PendingActions       { get; set; } = new();
    public DateTimeOffset?            LastSyncAt           { get; set; }
    public bool IsConfigured => ServerUrl != null && Username != null && Password != null;
}
