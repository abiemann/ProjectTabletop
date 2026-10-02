# Directional hatchling asset prompts

Generated with the built-in `image_gen.imagegen` tool, with `transparent_background: true`. Both final calls used the generated nine-part character study `exec-3127bcae-f018-4498-9f21-2a82cbc54a97.png` as a character/material reference. The study was derived from the original project `slot-dragons.png` and is not used at runtime.

## Frontal body atlas

```text
Create ONE production-ready transparent sprite atlas, not a mockup. The supplied image is a style and character reference ONLY, not a layout to preserve.
Exactly THREE headless dragon BODY components in a SINGLE HORIZONTAL ROW, green on left, red in center, blue on right. No other components and absolutely NO HEADS. Use the same rich jewel scales, ornate gold hornless neck, gold armored belly, wings, claws and shattered gilded egg design as the top row of the reference.
All three bodies face STRAIGHT FORWARD symmetrically, with two equal wings partly unfolded, two claws resting on egg rim and a short clean rounded scaled neck attachment above chest. No wounds or gore; these are parts for a layered animation rig.
Composition: wide landscape canvas with three equal columns. Each body has the same scale, center alignment and egg-bottom baseline. Each component occupies at most 70% of its column width, with LARGE EMPTY TRANSPARENT GUTTERS and generous clear space above/below. Fully include wings and every shell shard. Never let neighboring components touch. Render sharp intricate high-quality fantasy artwork matching reference.
True transparent RGBA background, all empty pixels exactly alpha0. Clean antialiasing at actual silhouettes only. No colored halos, no speckles in empty space, no checkerboard pixels, no shadows/glow/backdrop, no labels.
```

## Calm and firing head atlas

```text
Create ONE new production-ready transparent dragon HEAD sprite atlas. The supplied nine-part image is an identity and rendering-style reference ONLY, not a layout to preserve. Include no bodies, no wings, no eggs, only the following SIX heads.

STRICT THREE COLUMNS and TWO ROWS, six equal square cells on a landscape canvas. Columns are emerald green, ruby red, sapphire blue. Every head fully contained in the central 70% width and 70% height of its own cell, with at least15% empty transparent margin on all sides. LARGE CLEAR gutters between rows and columns. Equal head scale and exact horn/head/neck registration in all six cells.

TOP ROW: matching calm heads of these exact dragons, face looking directly forward with both amber eyes visible, closed mouth, gold horn crown, rich jewel scales, fierce appealing young dragon anatomy. Small short overlapping neck connector below jaw; stop the neck soon after the jaw, no long hanging torso.
BOTTOM ROW: same green/red/blue heads in downward-aimed fire-spitting pose, upper skull/horn crown registered with top row, head pitched slightly DOWN to focus on target below on page, jaw open showing fangs and dark throat, mouth lower than eyes, snout/eye direction clearly toward bottom of page. Do not face right or left. A little overlapping upper neck behind jaw. No actual fire, glow, particles or light effects: runtime supplies the fireball.

Keep original premium sharp painterly 3D fantasy detail, gold horns, ornate scale patterns and highlights, no simplification. Match original colors exactly.
All exterior and internal cutout background pixels exactly alpha0, true RGBA transparency. Clean antialiasing only immediately along actual silhouettes. No matte, shadow, colored fringe/speckles in empty areas, no checkerboard pixels, no text, no lines. Horns and every facial extremity completely visible with no cropping. This is a generously spaced animation-parts atlas, not an edge-to-edge poster.
```
