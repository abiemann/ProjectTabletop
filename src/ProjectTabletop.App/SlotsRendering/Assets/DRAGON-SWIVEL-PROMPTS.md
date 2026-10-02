# Dragon head swivel prompts

Generated with the built-in image tool on October 2, 2026. No CLI or external API was used. The final PNG is saved beside this file as `slot-dragon-heads-swivel.png`.

## Directional head atlas

Reference images: `slot-dragon-heads-front.png` (identity and materials), `slot-dragon-bodies-front.png` (neck attachment).

Use case: identity-preserve.
Asset type: high-quality transparent game sprite atlas for a smooth baby dragon HEAD SWIVEL animation.
Input image 1 is the exact dragon head identity/material reference; input image 2 is the existing frontal body to which these heads attach. Create only the new isolated HEADS, no bodies, no wings, no eggs.
Produce a precisely regular 5-column by 3-row sprite sheet, ideally 2560 x 1536 pixels, with equal square 512-pixel cells. 15 heads total.
Rows: emerald green dragon, ruby red dragon, sapphire blue dragon. Preserve their gold horns, forehead jewel, fine shiny scales, orange eyes, sharp teeth and rich illustrated 3D game-art finish exactly in the family of image 1.
Columns show a true anatomical YAW swivel about an UPRIGHT VERTICAL NECK axis: column 1 turns strongly toward image LEFT (65 degrees); column 2 turns moderately toward image LEFT (32 degrees); column 3 looks straight forward; column 4 turns moderately toward image RIGHT (32 degrees); column 5 turns strongly toward image RIGHT (65 degrees).
CRITICAL: no screen-plane rotation, no tilted crowns, no head roll. The heads stay upright like someone turning to look over their shoulder. Side poses must have real changed perspective, showing one side of the snout/cheek and correct occlusion of the far eye and horns. The snout extends toward the appropriate side, not a rotated flat front face.
All fifteen mouths are open in the same fire-breathing expression as the bottom row of image 1; point the snout slightly downward toward targets below the dragon. No actual fire, smoke or light effects in the sprites.
Registration: same camera scale in every cell. Neck socket centered at x=256, y=444 in each 512 cell, upper horn tips about y=40, all nontransparent pixels within x=24..488 and y=24..484. Keep the crown height and neck base stable through all views; only perspective changes. Allow the mouth to move naturally sideways around the fixed neck. No tall neck or torso extension.
Transparent exterior background with real alpha zero, including between horns and jaw silhouette. NO rendered checkerboard, no colored or black backdrop, no exterior glow or shadow, no grid lines, no labels, no text, no borders. Keep every sprite entirely in its own cell with ample empty transparent gutters, no clipped horns. Render beautifully detailed and crisp.

## Layout refinement

Reference: generated directional atlas `exec-e2f66d04-ad20-4fd5-8eff-6025b5dc788d.png`.

Edit this exact 5-column by 3-row dragon-head yaw atlas. Preserve every head's identity, color, pose, yaw angle, expression, scale relative to the other heads, and crisp detailed finish. Preserve the five views from image-left through front to image-right, and green/red/blue rows. Only improve the sprite layout for safe game rendering: make the canvas 2560 x 1536 with a precisely regular 5x3 grid of equal 512x512 cells, and uniformly reduce each head to fit comfortably INSIDE its own cell with at least 42 pixels empty transparent margin on all four sides. Do not allow horns, jaws or neck tips to touch cell boundaries. Keep the bottom centre of each neck socket registered at the exact same local point (256, 436), so the head swivels around a fixed upright neck. Keep vertical crown level consistent between views and rows. Heads must stay upright, no flat rotation or tilt. The mouths stay open. No actual fire.
CRITICAL OUTPUT: transparent PNG with real alpha. Every exterior background pixel must have alpha 0, including a clean entirely alpha-zero border and gutters. NO checkerboard in image pixels, no texture or color background, no exterior glow/shadow, no labels/grid/text. Transparent whitespace is intentional.

The generator returned 1619 × 971 pixels rather than the requested dimensions. Runtime source rectangles and landmarks register the actual pixels. Final source: `exec-ec0bfa95-dbcb-4e71-bd4c-011021b36584.png`.
