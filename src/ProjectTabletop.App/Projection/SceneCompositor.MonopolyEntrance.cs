using ProjectTabletop.Interaction;

namespace ProjectTabletop.App.Projection;

public sealed partial class SceneCompositor
{
    internal const double MonopolyEntranceLeadInMilliseconds = 320;
    internal const double MonopolyEntranceTileStaggerMilliseconds = 75;
    internal const double MonopolyEntranceTileFallMilliseconds = 420;
    internal const double MonopolyEntranceTileSettleMilliseconds = 120;
    internal const double MonopolyEntranceTileDurationMilliseconds = 540;
    internal const double MonopolyEntranceCenterStartMilliseconds = 3925;
    internal const double MonopolyEntranceCenterDurationMilliseconds = 1050;
    internal const double MonopolyEntranceDurationMilliseconds = 4975;

    private DateTimeOffset? _monopolyEntranceStartedAt;
    private bool _monopolyEntrancePending, _monopolyEntranceCompleted;
    internal long MonopolyEntranceRevision { get; private set; }

    internal sealed record MonopolyEntranceFrame(double ElapsedMilliseconds, bool Active,
        int LandedTiles, double CenterProgress);
    public sealed record MonopolyEntranceDiagnostics(bool Active, DateTimeOffset? StartedAt,
        double DurationMilliseconds, int LandedTiles, double CenterProgress, long Revision);

    public bool MonopolyEntranceActive
    {
        get { lock (_gate) return GetMonopolyEntranceFrame(_monopolyClock())?.Active == true; }
    }

    public MonopolyEntranceDiagnostics GetMonopolyEntranceDiagnostics()
    {
        lock (_gate)
        {
            var frame = GetMonopolyEntranceFrame(_monopolyClock());
            return new(frame?.Active == true, _monopolyEntranceStartedAt,
                MonopolyEntranceDurationMilliseconds, frame?.LandedTiles ?? 40,
                frame?.CenterProgress ?? 1, MonopolyEntranceRevision);
        }
    }

    private void OnBoardOpened(BoardScreen screen)
    {
        CancelCrownDeedDevelopment();
        CancelMonopolyEntrance();
        if (screen != BoardScreen.Monopoly) return;
        _monopolyEntrancePending = true;
        GetMonopolyEntranceFrame(_monopolyClock());
    }

    private void StartMonopolyEntrance(DateTimeOffset startedAt)
    {
        _monopolyEntrancePending = false;
        _monopolyEntranceCompleted = false;
        _monopolyEntranceStartedAt = startedAt;
        MonopolyEntranceRevision++;
        _handTips = [];
        _handFrameTime = DateTimeOffset.MinValue;
        ClearHandSpotlights();
        _boardSession.HoldMonopolyPresentationUntil(startedAt.AddMilliseconds(MonopolyEntranceDurationMilliseconds));
    }

    internal MonopolyEntranceFrame? GetMonopolyEntranceFrame(DateTimeOffset now)
    {
        if (_boardSession.Screen != BoardScreen.Monopoly) return null;
        if (_monopolyEntrancePending)
        {
            // A laptop-only board can animate without calibration. A projected
            // setup must finish its registration/reveal before the first tile.
            if (_blackOutput || _boardSetup || _calibrationTarget >= 0 || IsBoardRevealActive)
                return new(0, true, 0, 0);
            StartMonopolyEntrance(now);
        }
        if (_monopolyEntranceStartedAt is not { } started) return null;
        double elapsed = Math.Max(_monopolyEntranceCompleted ? MonopolyEntranceDurationMilliseconds : 0,
            (now - started).TotalMilliseconds);
        bool active = elapsed < MonopolyEntranceDurationMilliseconds;
        if (!active && !_monopolyEntranceCompleted)
        {
            _monopolyEntranceCompleted = true;
            // Release the shared hold and reject frames, pointing anchors and
            // execute events originating before the board finished landing.
            _boardSession.IsMonopolyPresentationActive(now);
            MonopolyEntranceRevision++;
        }
        int landed = Math.Clamp((int)Math.Floor((elapsed - MonopolyEntranceLeadInMilliseconds -
            MonopolyEntranceTileDurationMilliseconds) / MonopolyEntranceTileStaggerMilliseconds) + 1, 0, 40);
        return new(elapsed, active, landed, Math.Clamp((elapsed - MonopolyEntranceCenterStartMilliseconds) /
            MonopolyEntranceCenterDurationMilliseconds, 0, 1));
    }

    private int MonopolyEntranceRenderFrame(MonopolyEntranceFrame? frame) => frame?.Active == true
        ? (int)Math.Floor(frame.ElapsedMilliseconds * 60 / 1000) : -1;

    private void CancelMonopolyEntrance()
    {
        if (_monopolyEntranceStartedAt is null && !_monopolyEntrancePending) return;
        _monopolyEntranceStartedAt = null;
        _monopolyEntrancePending = _monopolyEntranceCompleted = false;
        MonopolyEntranceRevision++;
        _boardSession.CancelMonopolyPresentation(_monopolyClock());
    }
}
