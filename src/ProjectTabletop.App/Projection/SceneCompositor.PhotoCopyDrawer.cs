using ProjectTabletop.Interaction;

namespace ProjectTabletop.App.Projection;

public sealed partial class SceneCompositor
{
    public bool PhotoCopyDrawerOpen { get { lock (_gate) return _boardSession.PhotoCopyDrawerOpen; } }

    public sealed record PhotoCopyDrawerDiagnostics(bool Open, DateTimeOffset? OpenedAt,
        double DurationMilliseconds, double Progress, bool Animating);

    public bool TickPhotoCopy(DateTimeOffset now)
    {
        lock (_gate) return _boardSession.TickPhotoCopy(now);
    }

    public bool ActivatePhotoCopyButton(string id)
    {
        lock (_gate)
        {
            if (_boardSession.Screen != BoardScreen.PhotoCopy || IsBoardRevealActive) return false;
            bool changed = _boardSession.ActivateButton(id, _blackjackClock());
            if (changed) SyncPhotoCopySession();
            return changed;
        }
    }

    private bool HasPhotoCopyDrawerAnimation(DateTimeOffset now)
    {
        _boardSession.TickPhotoCopy(now);
        return _boardSession.PhotoCopyDrawerOpen && _boardSession.GetPhotoCopyDrawerProgress(now) < 1;
    }

    private bool HasBoardControlDrawerAnimation() => _boardSession.Screen switch
    {
        BoardScreen.Globe => HasGlobeDrawerAnimation(_globeClock()),
        BoardScreen.PhotoCopy => HasPhotoCopyDrawerAnimation(_blackjackClock()),
        BoardScreen.WaterGarden => HasWaterGardenDrawerAnimation(_waterClock()),
        _ => false
    };

    private int PhotoCopyDrawerVisualFrame(DateTimeOffset now) => _boardSession.PhotoCopyDrawerOpen
        ? 1 + (int)Math.Round(_boardSession.GetPhotoCopyDrawerProgress(now) * 100000) : 0;

    public PhotoCopyDrawerDiagnostics GetPhotoCopyDrawerDiagnostics(DateTimeOffset now)
    {
        lock (_gate)
        {
            bool animating = HasPhotoCopyDrawerAnimation(now);
            return new(_boardSession.PhotoCopyDrawerOpen, _boardSession.PhotoCopyDrawerOpenedAt,
                BoardSession.PhotoCopyDrawerOpeningDuration.TotalMilliseconds,
                _boardSession.GetPhotoCopyDrawerProgress(now), animating);
        }
    }
}
