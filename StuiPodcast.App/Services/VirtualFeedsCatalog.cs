namespace StuiPodcast.App.Services;

public static class VirtualFeedsCatalog
{
    public static readonly Guid All        = Guid.Parse("00000000-0000-0000-0000-00000000A11A");
    public static readonly Guid Saved      = Guid.Parse("00000000-0000-0000-0000-00000000A55A");
    public static readonly Guid Downloaded = Guid.Parse("00000000-0000-0000-0000-00000000D0AD");
    public static readonly Guid History    = Guid.Parse("00000000-0000-0000-0000-00000000B157");
    public static readonly Guid Queue      = Guid.Parse("00000000-0000-0000-0000-00000000C0DE");
    public static readonly Guid Seperator  = Guid.Parse("00000000-0000-0000-0000-00000000BEEF");

    public static bool IsVirtual(Guid id)
        => id == All || id == Saved || id == Downloaded || id == History
           || id == Queue || id == Seperator;
}
