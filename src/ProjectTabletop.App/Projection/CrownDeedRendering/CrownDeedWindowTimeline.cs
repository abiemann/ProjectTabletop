namespace ProjectTabletop.App.Projection.CrownDeedRendering;

// Presentation-only timing. These integer operations are mirrored in the
// pixel shader so every pane of an architectural window changes together.
// Neither selection nor subsequent dwell times consume gameplay randomness.
internal static class CrownDeedWindowTimeline
{
    internal static bool IsAnimated(int windowId) => windowId is > 0 and <= 65535 && Hash(windowId, 0) < 39321;

    internal static float LightLevel(int windowId, float elapsedSeconds)
    {
        if (!IsAnimated(windowId) || !float.IsFinite(elapsedSeconds)) return 1;
        float initialOn = 6 + Unit(windowId, 11) * 29;
        float elapsed = Math.Max(0, elapsedSeconds) - initialOn;
        if (elapsed <= 0) return 1;

        // A bounded block makes a distant injected time a constant-cost seek.
        // Fresh jitter and dwell/fade durations in each block avoid replaying
        // the same short loop. Every block ends lit before the next can begin.
        float period = 40 + Unit(windowId, 17) * 4;
        int cycle = (int)MathF.Floor(elapsed / period);
        float local = elapsed - cycle * period;
        float jitter = cycle == 0 ? 0 : Unit(windowId, CycleSalt(cycle, 1)) * 3;
        float fadeOut = .8f + Unit(windowId, CycleSalt(cycle, 2)) * 1.2f;
        float offDwell = 12 + Unit(windowId, CycleSalt(cycle, 3)) * 12;
        float fadeIn = .8f + Unit(windowId, CycleSalt(cycle, 4)) * 1.2f;
        float phase = local - jitter;
        if (phase <= 0) return 1;
        if (phase < fadeOut) return 1 - Ease(phase / fadeOut);
        phase -= fadeOut;
        if (phase < offDwell) return 0;
        phase -= offDwell;
        if (phase < fadeIn) return Ease(phase / fadeIn);
        return 1;
    }

    private static int CycleSalt(int cycle, int channel) => ((cycle % 65521) * 83 + channel * 997) % 65521;
    private static float Unit(int windowId, int salt) => Hash(windowId, salt) / 65535f;
    private static int Hash(int windowId, int salt)
    {
        // All intermediates are positive and below 2^24; there is no signed
        // overflow or CPU/GPU floating-point hash disagreement.
        int h = (windowId ^ (salt * 131)) & 65535;
        h = ((h * 251 + 13849) ^ (h >> 7)) & 65535;
        h = ((h * 173 + 24023) ^ (h >> 5)) & 65535;
        return h;
    }
    private static float Ease(float t) => t * t * (3 - 2 * t);
}
