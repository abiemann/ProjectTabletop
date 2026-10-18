using ProjectTabletop.Calibration;
using ProjectTabletop.Interaction;
using ProjectTabletop.Vision;

namespace ProjectTabletop.App.Projection;

public sealed partial class SceneCompositor
{
    // Opt this board into the shared hold veto without changing Enabled or its
    // caption palette: changing those would invalidate the acquisition scene
    // and erase the very search light that is keeping the reaching arm safe.
    private bool HoldButtonsBlockedByBoardActivity(DateTimeOffset now)
    {
        if (_boardSession.Screen != BoardScreen.Roulette || _boardCameraMap is null || _boardSurfaceMap is null)
            return false;
        if (_acquisitionHint is not null && now < _acquisitionLightUntil) return true;
        if (now >= _spotlightObservationFrameTime &&
            now - _spotlightObservationFrameTime <= TimeSpan.FromMilliseconds(350) &&
            _spotlightObservations.Any(observation => ReachesTable(observation.Hand))) return true;
        return _handSpotlights.Any(hand => SpotlightOpacity(now, hand.SourceFrameTime) > 0 && ReachesTable(hand.Hand));

        bool ReachesTable(HandDetection hand)
        {
            // Exclude the wrist and the footer's normal approach from the
            // viewer's edge. Fingers or palm farther onto the table veto it.
            for (int index = 1; index < hand.Landmarks.Count; index++)
            {
                var landmark = hand.Landmarks[index];
                if (!double.IsFinite(landmark.X) || !double.IsFinite(landmark.Y)) continue;
                Point2 board;
                try
                {
                    board = _boardSurfaceMap.InverseTransform(
                        _boardCameraMap.Transform(new Point2(landmark.X, landmark.Y)));
                }
                catch (InvalidOperationException) { continue; }
                if (double.IsFinite(board.X) && double.IsFinite(board.Y) &&
                    board.X is >= 0 and <= 1 && board.Y >= 0 &&
                    board.Y < BoardSession.RouletteExitBounds.Y - .015) return true;
            }
            return false;
        }
    }

    // Called at accepted light/hand arrival as well as checked when an
    // independent caption result publishes. A later release cannot revive an
    // old partial hold or a delayed clear frame from before the reaching arm.
    private void CancelHoldsForBoardActivity(DateTimeOffset frameTime)
    {
        if (!HoldButtonsBlockedByBoardActivity(HoldClock())) return;
        _boardSession.ResetHoldCaptionEvidence(
            _boardSession.Buttons.Where(button => button.IsHold).Select(button => button.Id).ToArray(), frameTime);
    }
}
