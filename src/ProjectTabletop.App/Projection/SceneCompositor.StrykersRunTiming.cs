#if DEBUG
using System.Diagnostics;
using ProjectTabletop.Interaction;

namespace ProjectTabletop.App.Projection;

public sealed partial class SceneCompositor
{
    private const int StrykersRunTimingCapacity = 3600;
    private readonly StrykersRunTimingFrame[] _strykersRunTimingFrames = new StrykersRunTimingFrame[StrykersRunTimingCapacity];
    private int _strykersRunTimingNext, _strykersRunTimingCount;
    private long _strykersRunTimingTotal, _strykersRunTextureSequence;
    private StrykersRunTextureTiming _strykersRunTextureTiming;

    private readonly record struct StrykersRunTextureTiming(float RoadPresentationDistance, float RunSeconds,
        float SimulationDistance, StrykersRunPhase Phase, StrykersRunStage Stage, long GameRevision,
        DateTimeOffset InputSourceUtc, long InputRevision);

    private readonly record struct StrykersRunTimingFrame(long Sequence, DateTimeOffset DrawStartedUtc,
        long DrawStartedTicks, long LockRequestedTicks, long LockAcquiredTicks, long DrawFinishedTicks,
        double Win2DTotalSeconds, double Win2DElapsedSeconds, bool RunningSlowly,
        bool TextureSubmitted, bool TextureReused, long TextureSequence,
        float RoadPresentationDistance, float RunSeconds, float SimulationDistance,
        StrykersRunPhase Phase, StrykersRunStage Stage, long GameRevision,
        DateTimeOffset RenderedInputSourceUtc, long RenderedInputRevision,
        DateTimeOffset InputSourceUtc, long InputRevision, int GcGen0, int GcGen1, int GcGen2)
    {
        public double DrawCpuMilliseconds => (DrawFinishedTicks - DrawStartedTicks) * 1000d / Stopwatch.Frequency;
        public double LockWaitMilliseconds => (LockAcquiredTicks - LockRequestedTicks) * 1000d / Stopwatch.Frequency;
        public double LockedDrawMilliseconds => (DrawFinishedTicks - LockAcquiredTicks) * 1000d / Stopwatch.Frequency;
    }

    // Called under _gate after the board texture is rendered, including any generic preview render.
    // Cache scalar values so a later projector cache hit describes the texture actually submitted.
    private void CaptureStrykersRunTextureTiming()
    {
        var state = _boardSession.StrykersRunState;
        _strykersRunTextureSequence++;
        _strykersRunTextureTiming = new(_boardSession.StrykersRunRoadPresentationDistance,
            state.RunSeconds, state.Distance, state.Phase, state.Stage, state.Revision,
            _strykersRunSourceTime, _strykersRunEpoch);
    }

    // Called only by the real projector Draw path, under _gate. The bounded value ring allocates
    // nothing per frame and performs no readback, file I/O, or serialization while drawing.
    private void RecordStrykersRunTiming(long started, long requested, long acquired, long finished,
        DateTimeOffset utc, TimeSpan totalTime, TimeSpan elapsedTime, bool runningSlowly, long previousTextureSequence)
    {
        bool submitted = _boardMediaClip is not null && _boardSurfaceMap is not null && StrykersRunArtworkReady &&
            _renderedBoardState is { Screen: BoardScreen.StrykersRun } && _strykersRunTextureSequence > 0;
        var texture = submitted ? _strykersRunTextureTiming : default;
        _strykersRunTimingFrames[_strykersRunTimingNext] = new(++_strykersRunTimingTotal, utc,
            started, requested, acquired, finished, totalTime.TotalSeconds, elapsedTime.TotalSeconds, runningSlowly,
            submitted, submitted && previousTextureSequence == _strykersRunTextureSequence, _strykersRunTextureSequence,
            texture.RoadPresentationDistance, texture.RunSeconds, texture.SimulationDistance,
            texture.Phase, texture.Stage, texture.GameRevision, texture.InputSourceUtc, texture.InputRevision,
            _strykersRunSourceTime, _strykersRunEpoch, GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2));
        _strykersRunTimingNext = (_strykersRunTimingNext + 1) % StrykersRunTimingCapacity;
        _strykersRunTimingCount = Math.Min(_strykersRunTimingCount + 1, StrykersRunTimingCapacity);
    }

    public object GetStrykersRunTiming()
    {
        lock (_gate)
        {
            var frames = new StrykersRunTimingFrame[_strykersRunTimingCount];
            int start = (_strykersRunTimingNext - _strykersRunTimingCount + StrykersRunTimingCapacity) % StrykersRunTimingCapacity;
            int first = Math.Min(_strykersRunTimingCount, StrykersRunTimingCapacity - start);
            Array.Copy(_strykersRunTimingFrames, start, frames, 0, first);
            Array.Copy(_strykersRunTimingFrames, 0, frames, first, _strykersRunTimingCount - first);
            return new
            {
                capacity = StrykersRunTimingCapacity, count = frames.Length, total = _strykersRunTimingTotal,
                overwritten = _strykersRunTimingTotal - frames.Length, stopwatchFrequency = Stopwatch.Frequency,
                phaseNames = Enum.GetNames<StrykersRunPhase>(), stageNames = Enum.GetNames<StrykersRunStage>(), frames
            };
        }
    }

    public void ClearStrykersRunTiming()
    {
        lock (_gate)
        {
            _strykersRunTimingNext = _strykersRunTimingCount = 0;
            _strykersRunTimingTotal = 0;
        }
    }
}
#endif
