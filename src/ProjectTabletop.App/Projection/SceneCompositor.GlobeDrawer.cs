using ProjectTabletop.Interaction;

namespace ProjectTabletop.App.Projection;

public sealed partial class SceneCompositor
{
    public bool GlobeDrawerOpen { get { lock (_gate) return _boardSession.GlobeDrawerOpen; } }

    public sealed record GlobeDrawerDiagnostics(bool Open, DateTimeOffset? OpenedAt,
        double DurationMilliseconds, double Progress, bool Animating);

    public bool TickGlobe(DateTimeOffset now)
    {
        lock (_gate) return _boardSession.TickGlobe(now);
    }

    private float GlobeDrawerProgress(DateTimeOffset now) => (float)_boardSession.GetGlobeDrawerProgress(now);

    private bool HasGlobeDrawerAnimation(DateTimeOffset now)
    {
        _boardSession.TickGlobe(now);
        return _boardSession.Screen == BoardScreen.Globe && _boardSession.GlobeDrawerOpen &&
            GlobeDrawerProgress(now) < 1;
    }

    public GlobeDrawerDiagnostics GetGlobeDrawerDiagnostics(DateTimeOffset now)
    {
        lock (_gate)
        {
            bool animating = HasGlobeDrawerAnimation(now);
            return new(_boardSession.GlobeDrawerOpen, _boardSession.GlobeDrawerOpenedAt,
                BoardSession.GlobeDrawerOpeningDuration.TotalMilliseconds, GlobeDrawerProgress(now), animating);
        }
    }
}
