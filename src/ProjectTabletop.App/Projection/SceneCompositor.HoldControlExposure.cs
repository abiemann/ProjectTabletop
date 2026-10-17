using ProjectTabletop.Vision;
using Windows.UI;

namespace ProjectTabletop.App.Projection;

public sealed partial class SceneCompositor
{
    // Shared by every board with long-press captions. When the camera sees a clipped,
    // unreadable caption without an obstruction, the board dims its light caption
    // element (a light plate, or light lettering on a dark plate) in bounded steps.
    private double _holdControlBrightness = 1;
    private long _holdControlNavigation = -1, _holdControlReset = -1;
    // Camera and calibration resets only; spotlight clears during animations do not count.
    private long _holdExposureResetCount;
    private DateTimeOffset _holdControlResetAt, _holdControlSettleUntil, _holdControlObservation;

    public double HoldControlBrightness
    {
        get { lock (_gate) { SyncHoldControlExposure(); return _holdControlBrightness; } }
    }

    private static Color HoldExposure(Color color, double brightness) => brightness >= 1 ? color :
        Color.FromArgb(color.A, (byte)Math.Round(color.R * brightness), (byte)Math.Round(color.G * brightness),
            (byte)Math.Round(color.B * brightness));

    private void ResetHoldControlExposure() => _holdExposureResetCount++;

    private void SyncHoldControlExposure()
    {
        if (_holdControlNavigation == _boardSession.NavigationRevision &&
            _holdControlReset == _holdExposureResetCount) return;
        _holdControlNavigation = _boardSession.NavigationRevision;
        _holdControlReset = _holdExposureResetCount;
        _holdControlSettleUntil = _holdControlObservation = default;
        if (_holdControlBrightness == 1) return;
        // Only a visible palette change makes earlier frames show the wrong plate.
        _holdControlResetAt = HoldClock();
        _holdControlBrightness = 1;
        _holdRevision++;
        _boardSession.ResetHoldCaptionEvidence();
        InvalidateBoardArtworkSurface();
    }

    private bool HoldControlsSettling(DateTimeOffset frameTime)
    {
        SyncHoldControlExposure();
        return frameTime < _holdControlResetAt || frameTime <= _holdControlSettleUntil;
    }

    internal bool ObserveHoldControlExposure(HoldButtonContext? context,
        HandAcquisitionPresenceResult? presence, DateTimeOffset frameTime)
    {
        lock (_gate)
        {
            var now = HoldClock();
            if (frameTime < _holdNavigationStartedAt || HoldControlsSettling(frameTime) || !AcquisitionBoardReady ||
                HasBoardControlDrawerAnimation() || context is null || !IsCurrentHoldContext(context) || frameTime > now ||
                now - frameTime > TimeSpan.FromMilliseconds(350) || frameTime <= _holdControlObservation ||
                presence?.TextPatterns is not { Count: > 0 } patterns) return false;
            _holdControlObservation = frameTime;
            // An unreadable, saturated caption may need less projected light.
            // Real obstruction or an active hold must never alter its reference.
            if (_holdControlBrightness <= MinimumLightLevel || presence.Hints.Count > 0 ||
                _boardSession.HoldProgress(now).Count > 0 || patterns.Any(pattern =>
                    pattern.CaptionHoldEvidence || pattern.ConfirmationFrames > 0 ||
                    pattern.ControlCoverage >= HandAcquisitionPresenceTracker.MinimumControlCoverage &&
                    pattern.ControlTriggerCoverage >= HandAcquisitionPresenceTracker.MinimumControlCoverage ||
                    pattern.CaptionReflectanceCoverage >= HandAcquisitionPresenceTracker.MinimumControlCoverage &&
                    pattern.CaptionReflectanceTriggerCoverage >= HandAcquisitionPresenceTracker.MinimumControlCoverage) ||
                !patterns.Any(pattern => pattern.CaptionClipped && !pattern.LabelIntact &&
                    pattern.ControlRegion >= 0 && pattern.ControlRegion < context.ButtonIds.Count &&
                    context.EnabledHoldIds.Contains(context.ButtonIds[pattern.ControlRegion]))) return false;
            _holdControlBrightness = Math.Max(MinimumLightLevel, _holdControlBrightness * LightLevelStep);
            // Camera timestamps describe delivery, not exposure. Reject delayed
            // views of our own brightness transition before rearming the caption.
            _holdControlSettleUntil = now + AcquisitionLightOffSettle;
            _boardSession.ResetHoldCaptionEvidence();
            _holdRevision++;
            InvalidateBoardArtworkSurface();
            return true;
        }
    }
}
