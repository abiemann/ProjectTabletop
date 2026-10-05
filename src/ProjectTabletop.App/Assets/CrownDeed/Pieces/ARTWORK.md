# Crown & Deed silver pieces

Original neutral game-piece artwork generated on 2026-10-04 with the built-in `image_gen.imagegen` tool, one call per asset with `transparent_background: true`. No stock images, brand marks, text, floor, external shadow, or display base were used. The renderer supplies the board shadow and rotates the pieces; image top is each directional piece's forward direction.

The shared art direction is detailed silver/pewter cast-metal miniatures with subtle patina, cool highlights, warm reflections, and a steep overhead camera. The prompt targets 78 degrees above horizontal; visual review confirms overhead directional pieces, while the hat is somewhat more oblique to retain the crown's readability. This is art direction, not a calibrated camera measurement.

All final PNGs are 1254 × 1254 RGBA. The only post-generation change cleared alpha value 1 to 0 to remove quantization dust. Every RGB value and every alpha value from 2 to 255 remains unchanged. Original generated outputs are retained. All four complete borders, corners, and representative exterior probes are alpha 0 for every asset. The gun's trigger guard and wheelbarrow's open handle/frame gaps were verified transparent. Recesses with metal or deck behind them, including the iron handle and ship railings, retain their opaque material.

## Files and framing

Bounds below are `[left, top, right, bottom]` in source pixels, with right/bottom exclusive. Crop bounds include four pixels of safety padding around the nonzero alpha support; retain physical aspect ratio when rendering.

| File | Suggested source crop | SHA-256 |
| --- | --- | --- |
| [hat.png](hat.png) | `[211, 119, 1042, 1111]` | `960ef8132ae0933a1a62236db8e17d0a8e46201fb33e111c3957fa4353c02b41` |
| [car.png](car.png) | `[295, 74, 959, 1177]` | `e1b205a284b21fae053ceff0a0ca3081af2c549b015e7477415d1ce4b08490c7` |
| [shoe.png](shoe.png) | `[425, 84, 822, 1181]` | `633a4fcbb007132f8ac6f489d77228b1cb9de2a49df4b0d6fa82a5138f94c6f3` |
| [dog.png](dog.png) | `[414, 48, 842, 1146]` | `ca88f27e2076a03ac33d71ea129a21b185b1a350790ff38ad5ea80b340f9c26f` |
| [gun.png](gun.png) | `[452, 41, 905, 1181]` | `df50ffde095d60a38947427db053ad26ee55be2c9d95686ab62bd2888028864b` |
| [iron.png](iron.png) | `[287, 36, 968, 1207]` | `7dcd02d753b5c5adbffcf59b4d5782744be1a9eab1cfa9faaab4e203b5dcaca0` |
| [wheelbarrow.png](wheelbarrow.png) | `[351, 2, 904, 1238]` | `b50cf818cc65a773959560d194c123f5fb1979f34d37765e6a44407d76086263` |
| [steamship.png](steamship.png) | `[488, 26, 774, 1211]` | `0617480f0c7e72fd342ca155c8d0559535a1299e881b9e98487458cf332914d5` |

## Generation prompts

Each exact generation prompt consists of the following shared text followed by that asset's subject text below.

### Shared text

```text
Use case: stylized-concept. Asset type: original premium digital tabletop board-game piece, a single isolated full object. Render one finely crafted cast silver / light pewter miniature with smooth bevels, restrained patina only in recesses, precise high-end photorealistic 3D product-render materials and crisp specular detail. Mature elegant collectible-metal quality, neutral cool silver with very subtle warm reflections; no colored enamel. Consistent camera: orthographic near-overhead, optical axis tilted just 12 degrees from vertical (78 degrees above the horizontal tabletop), centered, no camera roll, mostly seeing the TOP surfaces with just a little rear side depth at the BOTTOM of the image. The object's forward direction is exactly screen UP, never diagonal and never toward the viewer. Full object centered in a square 1024x1024 image, comfortably inside 12 percent transparent margin on every side, no cutoffs. Large upper-left studio softbox with a crisp smaller upper-right rim reflection. Real RGBA transparency: all empty exterior pixels alpha 0, clean antialiasing, any enclosed holes fully transparent. No floor, no contact shadow, no drop shadow, no backdrop, no checkerboard, no text, no logos, no brand marks, no display pedestal, no plinth, no oval base, no extra objects. The app adds its own shadow later. Keep the silhouette readable as a small token.
```

### hat

```text
 Subject: a single original classic tall TOP HAT miniature, upright; oval brim and gently tapered cylindrical crown, shallow inset top and a restrained smooth hatband cast in the same silver. Its long brim axis is vertical in the image with its front toward screen top; show enough rear crown wall from this steep camera to make the hat unmistakable. Entire brim intact.
```

### car

```text
 Subject: a single original compact vintage OPEN ROADSTER car miniature, front hood and nose pointing exactly toward the TOP edge, rear at the BOTTOM edge. Distinct long sculpted hood, tiny two-seat open cockpit, low windshield, rounded front fenders, four small solid metal wheels, subtle grille engraving. Everything cast in silver; no real manufacturer design cues or badges. Carefully modeled top surfaces and open cockpit make it legible from overhead. Preserve all wheels and fenders.
```

### shoe

```text
 Subject: a single original elegant lace-up leather OXFORD SHOE translated entirely into cast silver metal. The TOE points exactly to the TOP edge; the open collar, heel and back are at the BOTTOM edge. Show the rounded toe cap, modeled rows of small eyelets, crossing silver laces, stitched seam engraving, thin sole rim and a subtle heel. One shoe only, no pair, no foot. The collar is a real dark recessed interior, not a floating black insert. From the steep overhead camera the long toe points straight up and the full silhouette remains visible.
```

### dog

```text
 Subject: a single original small alert TERRIER DOG figurine standing naturally on all four paws, HEAD and MUZZLE directed exactly toward the TOP edge, body runs straight downward to rump and short raised tail toward the BOTTOM. Near-overhead view from behind and above: clearly show the back, two pointed ears, subtly turned-up muzzle silhouette, all four short sturdy paws slightly splayed, raised tail and carefully sculpted wavy fur. Keep the identity dog-like, not wolf-like, with no cartoon facial features; full anatomical metal miniature including natural transparent gaps between paws. No collar tag or lettering.
```

### gun

```text
 Subject: a single small NON-FUNCTIONAL decorative antique PISTOL miniature game token, lying on its LEFT SIDE so the engraved right side and the open trigger guard remain readable from this steep overhead camera. Long slim barrel points EXACTLY toward the TOP edge, vertical, with muzzle at the TOP; curved grip projects toward lower RIGHT. Original simplified old-fashioned flintlock-inspired silhouette, all silver metal including grip, a small sculpted hammer, deep simple engraving, rounded open trigger guard with a genuinely transparent hole. No ammunition, no bullets, no hand, no brand, no detachable parts, no background.
```

### iron

```text
 Subject: a single original traditional HAND-IRON miniature, old-fashioned heavy household clothes iron. The pointed triangular NOSE faces exactly toward the TOP edge; the broader rounded heel is at the BOTTOM. A finely modeled flat soleplate, smoothly raised metal body and a simple arched open grip handle running front-to-back above the top; the handle's open space is clearly modeled with any through-holes genuinely transparent. All silver cast metal, no electric cord, no colored plastic, no logos, no text. Make the triangular pointed iron silhouette unmistakable from the steep overhead view.
```

### wheelbarrow

```text
 Subject: a single original traditional GARDEN WHEELBARROW miniature. Its single front wheel and narrow nose point exactly toward the TOP edge; its two long grip handles extend symmetrically toward the BOTTOM edge. An empty deep rounded trapezoid metal basin occupies the middle, a single silver wheel ahead, two fine supporting legs beneath the rear, and two continuous silver frame rails running down to the handles. Everything cast in matching silver, no rubber or wood colors. The steep near-overhead view must clearly show the hollow basin and the open spaces between the two lower handles and frame; where there is no metal these spaces must be truly transparent. Full wheel, basin, handles and legs visible, no contents.
```

### steamship

```text
 Subject: a single original classic ocean STEAMSHIP miniature, seen from the fixed steep near-overhead camera. Long narrow hull runs vertically with pointed BOW exactly toward the TOP edge and rounded STERN at the BOTTOM. Two short round smokestacks along the central axis, a compact raised bridge near the bow, carefully sculpted stepped deckhouses, small lifeboats and fine simplified railing supports, all cast in the same silver metal. Keep the silhouette unmistakably a ship from overhead, entirely intact with both pointed bow and stern. No smoke, no water, no wake, no flag, no writing, no brand marks, no stand. Any open spaces outside the hull and between free-standing external supports must be genuinely transparent.
```

## Verification evidence

Local development evidence is retained in the ignored `artifacts/crown-deed-silver-pieces-20261004/` directory: `generation-prompts.json` contains the full exact prompt for every asset and original output path; `alpha-original-report.json` preserves original-alpha measurements; `alpha-final-manifest.json` records original/final hashes, cleanup counts, source bounds, and alpha probes. `silver-pieces-contact.png` previews the complete set on dark and light backgrounds. The inspection and finalization scripts are retained alongside the reports. These preview images do not replace or modify the source assets.
