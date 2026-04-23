using StuiPodcast.App.Services;
using StuiPodcast.App.UI;

namespace StuiPodcast.App.Command.UseCases;

// :undo — pop the most recent undoable action. No redo stack; that's a
// classic feature-creep trap given how coarse the actions are ("restore
// queue" vs "restore episode 3 at position 4"). Keep it simple.
internal sealed class UndoUseCase
{
    readonly IUiShell _ui;
    readonly UndoStack _stack;

    public UndoUseCase(IUiShell ui, UndoStack stack)
    {
        _ui = ui;
        _stack = stack;
    }

    public void Exec(string[] args)
    {
        var reverted = _stack.Pop();
        if (reverted == null) { _ui.ShowOsd("undo: nothing to undo", 1200); return; }
        _ui.ShowOsd($"undo: {reverted}", 1800);
    }
}
