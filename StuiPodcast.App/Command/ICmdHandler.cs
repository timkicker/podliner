namespace StuiPodcast.App.Command;

internal interface ICmdHandler
{
    bool CanHandle(TopCommand kind);
    void Handle(CmdParsed cmd, CmdContext ctx);
}