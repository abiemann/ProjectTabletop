using System.Text;

namespace ProjectTabletop.App.Media;

internal static class PhotocopierWave
{
    // Original synthesized motor, scanner and paper-feed sound; no external asset.
    internal static byte[] Create()
    {
        const int sampleRate = 24000, samples = sampleRate * 2;
        using var stream = new MemoryStream(44 + samples * 2);
        using var writer = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true);
        writer.Write("RIFF"u8); writer.Write(36 + samples * 2); writer.Write("WAVEfmt "u8);
        writer.Write(16); writer.Write((short)1); writer.Write((short)1);
        writer.Write(sampleRate); writer.Write(sampleRate * 2);
        writer.Write((short)2); writer.Write((short)16);
        writer.Write("data"u8); writer.Write(samples * 2);

        var noise = new Random(620);
        ReadOnlySpan<double> clicks = [.04, 1.04, 1.83];
        double motorPhase = 0, scannerPhase = 0, filteredNoise = 0;
        for (int i = 0; i < samples; i++)
        {
            double t = (double)i / sampleRate;
            double white = noise.NextDouble() * 2 - 1;
            filteredNoise += .14 * (white - filteredNoise);
            double motor = Envelope(t, .03, 1.78, .13);
            double spin = Math.Clamp(t / .2, 0, 1) * Math.Clamp((1.82 - t) / .3, 0, 1);
            motorPhase += Math.Tau * (62 + 36 * spin) / sampleRate;
            double scan = Envelope(t, .2, 1.25, .12);
            scannerPhase += Math.Tau * (350 + 150 * Math.Sin(Math.PI * (t - .2) / 1.05)) / sampleRate;
            double feed = Envelope(t, 1.05, 1.83, .09);
            double rollers = .72 + .28 * Math.Sin(Math.Tau * 22 * t);
            double signal = motor * (.15 * Math.Sin(motorPhase) + .035 * Math.Sin(3 * motorPhase)) +
                scan * (.045 * Math.Sin(scannerPhase) + .14 * filteredNoise) +
                feed * rollers * (.21 * filteredNoise + .035 * white);
            // Short relay/roller clicks frame the mechanical cycle.
            foreach (double click in clicks)
            {
                double elapsed = t - click;
                if (elapsed >= 0 && elapsed < .075)
                    signal += .2 * Math.Exp(-elapsed * 75) *
                        (.65 * Math.Sin(Math.Tau * 185 * elapsed) + .35 * white);
            }
            writer.Write((short)Math.Round(Math.Clamp(signal, -.8, .8) * short.MaxValue));
        }
        return stream.ToArray();
    }

    private static double Envelope(double time, double start, double end, double fade) =>
        Math.Clamp((time - start) / fade, 0, 1) * Math.Clamp((end - time) / fade, 0, 1);
}
