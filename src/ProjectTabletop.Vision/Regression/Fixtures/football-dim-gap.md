# Football marker on a dim pitch

These camera photographs were captured on 2026-10-16 at 16:51:39 UTC (held)
and 16:53:54 UTC (clear) with the same 1920x1080 camera and guided Football
profile. Only rectangle (800,650)-(1440,1010) is retained. Pixels are unchanged;
the test rescales normalized marker area to retain its original pixel geometry.

The held marker has two black strips with max-RGB core values 0–1, while its
real separating surface is 50–71 and the darker outer flank is 62–75 at the
seven sampled positions. All gap pixels fall below the trained ordinary cutoff
75.65, although the existing darker mask measures two valid 62x19-pixel strips.
The old gap test rejected the pair. The clear crop must remain empty, and the
held crop supplied as its own projection reference must still be rejected.
These are detector regression photographs, not game artwork.
