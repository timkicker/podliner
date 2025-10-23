namespace StuiPodcast.App.Command;

internal interface ICommandHandler
{
    bool CanHandle(TopCommand kind);
    void Handle(ParsedCommand cmd, CommandContext ctx);
}