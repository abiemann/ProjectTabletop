# Roulette materials

## Burl wood

- Asset: [roulette-burl-wood.png](roulette-burl-wood.png).
- Created: 2026-10-05 with the built-in `image_gen.imagegen` tool, one generation call, `transparent_background: false`.
- Purpose: original diffuse-style color material for the separately rendered roulette bowl and mechanism. The app supplies geometry, lighting, finish reflections, and motion.
- Actual output: 1254 × 1254, opaque RGB PNG, 2,872,270 bytes. The prompt requested 2048 square; the built-in tool returned the actual dimensions recorded here.
- SHA-256: `6a9fa4e6411545d1e4715ead27f6a8b008d70b8502fb1a6be0dd140f087c9e49`.
- Original generated output: `exec-fe93491a-7a16-45d2-9055-90bad2520e1f.png` under the Codex generated-images directory. The project file is an unchanged byte-for-byte copy; the original is retained.
- No source photograph was supplied to the generation call. This is original generated wood grain with no brand marks, copied image pixels, lettering, or wheel components.
- Inspection: full image plus 384, 192, and 96 pixel previews. Natural asymmetric grain and small burl eyes remain legible at reduced scale. Even diffuse exposure, no baked specular highlight, shadow, floor, seams, blur, or objects. The RGB output is fully opaque; no transparency was requested.
- Mapping: one coherent wood slab; seamless tiling is not assumed. Use as a material on independently rendered stationary and moving parts, preserving their separate transforms.

### Exact generation prompt

```text
Use case: product-mockup. Asset type: original production PBR-style diffuse/albedo wood material texture for a beautifully crafted luxury roulette wheel bowl. Primary request: one opaque square 2048x2048 flat orthographic material scan of an unbroken French-polished amboyna and walnut burl wood slab. The wood itself fills every pixel edge to edge. A genuinely natural coherent piece of premium figured hardwood: intricate flowing flame-like russet and honey grain, subtle warm amber ribbons, chocolate-brown meandering veins, and scattered small irregular burl eyes. Mature fine furniture craftsmanship with rich but restrained color and contrast, readable micrograin and crisp pore detail. Use natural variation in scale, spacing and direction; the grain swirls are organic and asymmetrical rather than repeated evenly. The polishing is communicated by richness and clarity of grain ONLY, as an even diffuse albedo material; the app adds the actual highlights. Perfectly even diffuse illumination over the entire image, uniform exposure, sharp focus everywhere, no directional highlights, no light-source reflections, no gradients, no vignetting, no shadows, no depth perspective. Single coherent slab, not multiple planks, no seams or joints. No wheel, no furniture, no props, no objects, no numbers, no lettering, no logo, no watermark, no border. No zebra stripes, no marble or stone, no paint, no blur, no blown-out orange, no artificial symmetrical pattern, no rendered shine, no transparent pixels. Fully opaque RGB/RGBA material image. This is original generated wood grain, not a reproduction of any existing photograph.
```

### Local inspection evidence

Ignored development preview: `artifacts/roulette-materials-20261005/burl-scale-preview.png`. This contact sheet is for inspection only; the production texture was not resized, recolored, or otherwise edited.
