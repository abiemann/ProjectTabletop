namespace ProjectTabletop.Interaction;

public sealed partial class BoardSession
{
    private static TimeSpan BottomDrawerOpeningDuration => TimeSpan.FromMilliseconds(300);
    private static readonly BoardRect BottomDrawerHandleBounds = new(.01, .87, .18, .12);

    // The edge drawers use the same entrance and fresh-input policy. Their
    // contents and action semantics remain owned by their respective boards.
    private sealed class BottomDrawerState
    {
        public bool Open;
        public bool OpeningReady;
        public DateTimeOffset? OpenedAt;
        public DateTimeOffset ObservedAt = DateTimeOffset.MinValue;
        public DateTimeOffset InputReadyAfter = DateTimeOffset.MinValue;
    }

    private readonly BottomDrawerState _globeDrawer = new();
    private readonly BottomDrawerState _photoCopyDrawer = new();
    private readonly BottomDrawerState _waterGardenDrawer = new();

    private double BottomDrawerProgress(BottomDrawerState drawer, BoardScreen screen, DateTimeOffset now) =>
        Screen == screen && drawer.Open && drawer.OpenedAt is { } opened
            ? drawer.OpeningReady ? 1 : Math.Clamp((now - opened) / BottomDrawerOpeningDuration, 0, 1)
            : 0;

    private bool SelectBottomDrawer(BottomDrawerState drawer, BoardScreen screen, bool open, DateTimeOffset now)
    {
        if (Screen != screen || now < drawer.ObservedAt || drawer.Open == open) return false;
        if (open)
        {
            drawer.Open = true;
            drawer.OpenedAt = now;
            drawer.OpeningReady = false;
        }
        else ClearBottomDrawerUi(drawer);
        BottomDrawerInputBarrier(drawer, now);
        return true;
    }

    private bool AdvanceBottomDrawer(BottomDrawerState drawer, BoardScreen screen, DateTimeOffset now)
    {
        drawer.ObservedAt = Later(drawer.ObservedAt, now);
        if (Screen != screen || !drawer.Open || drawer.OpeningReady || drawer.OpenedAt is not { } opened ||
            now < opened + BottomDrawerOpeningDuration) return false;
        drawer.OpeningReady = true;
        // A moving caption/gesture cannot become an action as it settles. The
        // deadline, rather than a late render tick, is the first usable boundary.
        BottomDrawerInputBarrier(drawer, opened + BottomDrawerOpeningDuration);
        return true;
    }

    private void AdvanceBottomDrawers(DateTimeOffset now)
    {
        AdvanceBottomDrawer(_globeDrawer, BoardScreen.Globe, now);
        AdvanceBottomDrawer(_photoCopyDrawer, BoardScreen.PhotoCopy, now);
        AdvanceBottomDrawer(_waterGardenDrawer, BoardScreen.WaterGarden, now);
    }

    private bool BottomDrawerHoldFrameIsCurrent(DateTimeOffset frameTime) => Screen switch
    {
        BoardScreen.Globe => frameTime > _globeDrawer.InputReadyAfter,
        BoardScreen.PhotoCopy => frameTime > _photoCopyDrawer.InputReadyAfter,
        BoardScreen.WaterGarden => frameTime > _waterGardenDrawer.InputReadyAfter,
        _ => true
    };

    private void ClearBottomDrawerUi(BottomDrawerState drawer)
    {
        if (drawer.Open) Revision++;
        drawer.Open = false;
        drawer.OpenedAt = null;
        drawer.OpeningReady = false;
    }

    private void ClearBottomDrawers(DateTimeOffset now)
    {
        foreach (var drawer in new[] { _globeDrawer, _photoCopyDrawer, _waterGardenDrawer })
        {
            ClearBottomDrawerUi(drawer);
            drawer.ObservedAt = Later(drawer.ObservedAt, now);
            drawer.InputReadyAfter = Later(drawer.InputReadyAfter, now);
        }
    }

    private void BottomDrawerInputBarrier(BottomDrawerState drawer, DateTimeOffset now)
    {
        drawer.InputReadyAfter = Later(drawer.InputReadyAfter, now);
        BottomDrawerGestureBarrier(now);
        // Preserve spent places: the fingers that opened ^ cannot close its v
        // replacement, or activate a newly displayed result, without lifting.
        ResetHoldCaptionEvidence();
    }

    private void BottomDrawerGestureBarrier(DateTimeOffset now)
    {
        Revision++;
        _ignoreExecutionsThrough = Later(_ignoreExecutionsThrough, now);
        _ignoreSelectionsThrough = Later(_ignoreSelectionsThrough, now);
        _ignoreFramesThrough = Later(_ignoreFramesThrough, now);
        HoveredButtonIds = Array.Empty<string>();
        InvalidateFingerSelection(now);
    }
}
