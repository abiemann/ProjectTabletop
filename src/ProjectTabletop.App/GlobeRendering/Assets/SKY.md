# Globe sky artwork

`milky-way-sky.png` is the first original illustrative sky regenerated with the built-in image-generation tool on 2026-10-17. It preserves the earlier blue-violet diagonal Milky Way, with a dense star field and natural-looking dust lanes. The user chose this version after rejecting the second, cloud-only rendition as unnatural. The user subsequently rejected this first rendition too and requested NASA Deep Star Maps next. It is not a scientifically mapped sky or a NASA image. Earth and cloud sources are documented separately.

The generated sky source is an opaque RGB PNG, 1672 × 941 pixels. SHA-256: `4C036E75AD28E1A7C2DE31797C86D360111EC20ED4702A04B0BF988602D0D032`. The tool returned this size even when asked for 3840 × 2160; this source is not claimed to be native 4K. Its embedded stars share that source resolution; the renderer supplements them with native-resolution fine points.

The renderer crops the sky texture to the physical board aspect and draws a separate, deterministic field of individual stars directly at the output's native pixel density. Fine cores, varied colour/intensity, restrained halos and broad irregular associations add genuine detail. Physical short-edge coordinates preserve round points on different board shapes. The second rendition's extra lower-edge brightness compensation is removed. Both layers are cached together; Earth rotation, zoom and comet motion do not regenerate or animate the sky, keeping the swipe reference stationary.

Runtime shading keeps the top and bottom quarters fully bright. Smooth inward transitions over 25–32.5% and 67.5–75% of board height meet the unchanged 50% RGB-intensity centre. Fine stationary dithering softens tonal seams. The brightness mask affects only the sky; Earth, its atmosphere, comets and controls retain their own shading.

Occasional comets travel 70% farther at their earlier velocity, lasting 1.36–2.04 seconds, with tails 30% longer. Their thin warm-white/blue-gold trail remains behind Earth and above the stable bottom-quarter swipe area.

Generation brief for the selected first rendition (summary; built-in image-generation tool, earlier sky used as the edit reference):

```text
Regenerate a sharper, richly detailed star-filled sky for a 4K projector, targeting 3840 × 2160. Preserve the much-loved central diagonal blue-violet Milky Way and its dusty galactic light, while improving fine star detail along the long edges.
```
