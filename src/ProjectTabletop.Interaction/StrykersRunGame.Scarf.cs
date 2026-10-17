namespace ProjectTabletop.Interaction;

public sealed partial class StrykersRunGame
{
    // Measure real travel from the last turning point, not single input samples:
    // tiny rail jitter cannot flip the scarf, while slow deliberate turns can.
    private const float ScarfDirectionTravel = .006f;
    private const float ScarfTurnSeconds = .22f;
    private float _scarfSide = -1, _scarfTargetSide = -1, _scarfTurnStart = -1;
    private float _scarfTurnAge = ScarfTurnSeconds, _scarfMotionAnchorX = .5f;

    private void ResetScarf()
    {
        _scarfSide = _scarfTargetSide = _scarfTurnStart = -1;
        _scarfTurnAge = ScarfTurnSeconds;
        _scarfMotionAnchorX = _squadX;
    }

    private void StepScarf()
    {
        float travel = _squadX - _scarfMotionAnchorX;
        bool reversed = _scarfTargetSide < 0 ? travel < -ScarfDirectionTravel : travel > ScarfDirectionTravel;
        if (reversed)
        {
            _scarfTurnStart = _scarfSide;
            _scarfTargetSide = -_scarfTargetSide;
            _scarfTurnAge = 0;
            _scarfMotionAnchorX = _squadX;
        }
        else if (_scarfTargetSide < 0 ? travel > 0 : travel < 0)
            _scarfMotionAnchorX = _squadX;

        _scarfTurnAge = Math.Min(ScarfTurnSeconds, _scarfTurnAge + StepSeconds);
        float progress = _scarfTurnAge / ScarfTurnSeconds;
        float smooth = progress * progress * (3 - 2 * progress);
        _scarfSide = _scarfTurnStart + (_scarfTargetSide - _scarfTurnStart) * smooth;
    }
}
