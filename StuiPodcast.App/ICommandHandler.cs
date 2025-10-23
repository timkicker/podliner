namespace StuiPodcast.App;

internal interface ICommandHandler
{
    bool CanHandle(TopCommand kind);
    void Handle(ParsedCommand cmd, CommandContext ctx);
}