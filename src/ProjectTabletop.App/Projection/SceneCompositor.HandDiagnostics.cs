namespace ProjectTabletop.App.Projection;

public sealed partial class SceneCompositor
{
    // Retain the successful route across navigation, which clears hover/arming
    // feedback. This is diagnostic evidence only and cannot replay a command.
    private object? _lastHandBoardSelection;

    public object? GetLastHandBoardSelection()
    {
        lock (_gate) return _lastHandBoardSelection;
    }
}
