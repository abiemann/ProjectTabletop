using ProjectTabletop.Interaction;

namespace ProjectTabletop.App.Projection;

public sealed partial class SceneCompositor
{
    internal const double CrownDeedEntranceLeadInMilliseconds = 320;
    internal const double CrownDeedEntranceTileStaggerMilliseconds = 75;
    internal const double CrownDeedEntranceTileFallMilliseconds = 420;
    internal const double CrownDeedEntranceTileSettleMilliseconds = 120;
    internal const double CrownDeedEntranceTileDurationMilliseconds = 540;
    internal const double CrownDeedEntranceCenterStartMilliseconds = 3925;
    internal const double CrownDeedEntranceCenterDurationMilliseconds = 1050;
    internal const double CrownDeedEntranceDurationMilliseconds = 4975;

    private DateTimeOffset? _crownDeedEntranceStartedAt;
    private DateTimeOffset? _crownDeedEntranceEarliestStart;
    private bool _crownDeedEntrancePending, _crownDeedEntranceCompleted;
    internal long CrownDeedEntranceRevision { get; private set; }

    internal sealed record CrownDeedEntranceFrame(double ElapsedMilliseconds, bool Active,
        int LandedTiles, double CenterProgress);
    public sealed record CrownDeedEntranceDiagnostics(bool Active, DateTimeOffset? StartedAt,
        double DurationMilliseconds, int LandedTiles, double CenterProgress, long Revision);

    public bool CrownDeedEntranceActive
    {
        get { lock (_gate) return GetCrownDeedEntranceFrame(_crownDeedClock())?.Active == true; }
    }

    public CrownDeedEntranceDiagnostics GetCrownDeedEntranceDiagnostics()
    {
        lock (_gate)
        {
            var frame = GetCrownDeedEntranceFrame(_crownDeedClock());
            return new(frame?.Active == true, _crownDeedEntranceStartedAt,
                CrownDeedEntranceDurationMilliseconds, frame?.LandedTiles ?? 40,
                frame?.CenterProgress ?? 1, CrownDeedEntranceRevision);
        }
    }

    private void OnBoardOpened(BoardScreen screen)
    {
        CancelCrownDeedDevelopment();
        CancelCrownDeedEntrance();
        if (screen != BoardScreen.CrownDeed) return;
        _crownDeedEntrancePending = true;
        GetCrownDeedEntranceFrame(_crownDeedClock());
    }

    private void StartCrownDeedEntrance(DateTimeOffset startedAt)
    {
        if (!CrownDeedResourcesReady)
        {
            // Registration can schedule a future entrance while the artwork
            // is decoding. Keep that deadline, then start when both are ready.
            _crownDeedEntrancePending = true;
            _crownDeedEntranceEarliestStart = startedAt;
            return;
        }
        _crownDeedEntrancePending = false;
        _crownDeedEntranceEarliestStart = null;
        _crownDeedEntranceCompleted = false;
        _crownDeedEntranceStartedAt = startedAt;
        CrownDeedEntranceRevision++;
        _handTips = [];
        _handFrameTime = DateTimeOffset.MinValue;
        ClearHandSpotlights();
        _boardSession.HoldCrownDeedPresentationUntil(startedAt.AddMilliseconds(CrownDeedEntranceDurationMilliseconds));
    }

    internal CrownDeedEntranceFrame? GetCrownDeedEntranceFrame(DateTimeOffset now)
    {
        if (_boardSession.Screen != BoardScreen.CrownDeed) return null;
        if (_crownDeedEntrancePending)
        {
            // A laptop-only board can animate without calibration. A projected
            // setup must finish its registration/reveal before the first tile.
            if (!CrownDeedResourcesReady || _blackOutput || _boardSetup || _calibrationTarget >= 0 || IsBoardRevealActive)
                return new(0, true, 0, 0);
            StartCrownDeedEntrance(_crownDeedEntranceEarliestStart is { } earliest && earliest > now ? earliest : now);
        }
        if (_crownDeedEntranceStartedAt is not { } started) return null;
        double elapsed = Math.Max(_crownDeedEntranceCompleted ? CrownDeedEntranceDurationMilliseconds : 0,
            (now - started).TotalMilliseconds);
        bool active = elapsed < CrownDeedEntranceDurationMilliseconds;
        if (!active && !_crownDeedEntranceCompleted)
        {
            _crownDeedEntranceCompleted = true;
            // Release the shared hold and reject frames, pointing anchors and
            // execute events originating before the board finished landing.
            _boardSession.IsCrownDeedPresentationActive(now);
            CrownDeedEntranceRevision++;
        }
        int landed = Math.Clamp((int)Math.Floor((elapsed - CrownDeedEntranceLeadInMilliseconds -
            CrownDeedEntranceTileDurationMilliseconds) / CrownDeedEntranceTileStaggerMilliseconds) + 1, 0, 40);
        return new(elapsed, active, landed, Math.Clamp((elapsed - CrownDeedEntranceCenterStartMilliseconds) /
            CrownDeedEntranceCenterDurationMilliseconds, 0, 1));
    }

    private int CrownDeedEntranceRenderFrame(CrownDeedEntranceFrame? frame) => frame?.Active == true
        ? (int)Math.Floor(frame.ElapsedMilliseconds * 60 / 1000) : -1;

    private void CancelCrownDeedEntrance()
    {
        if (_crownDeedEntranceStartedAt is null && !_crownDeedEntrancePending) return;
        _crownDeedEntranceStartedAt = null;
        _crownDeedEntranceEarliestStart = null;
        _crownDeedEntrancePending = _crownDeedEntranceCompleted = false;
        CrownDeedEntranceRevision++;
        _boardSession.CancelCrownDeedPresentation(_crownDeedClock());
    }
}
