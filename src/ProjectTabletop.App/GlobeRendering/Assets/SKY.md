# Globe sky artwork

`milky-way-sky.png` is original artwork generated with the built-in image-generation tool on 2026-10-17, using the user's supplied Milky Way image as visual inspiration. It is an illustrative deep-space backdrop, not a scientifically mapped sky or a NASA image. The Earth surface and cloud sources are documented separately.

The generated source is an opaque RGB PNG, 1672 × 941 pixels (not native 4K). SHA-256: `5C6053CDE7161765B14B8AE792632E86F897A741E142CC0087B745D18CE7124F`.

The app loads the opaque image with its other Globe textures and caches an aspect-preserving crop for the board, scaling the source to the output resolution. The background stays stationary while Earth rotates and zooms. Runtime sky shading keeps the top 12.5% at its original intensity, smoothly reduces it to 50% RGB intensity by 25% of board height, preserves that dim level through the midpoint, and smoothly restores the original intensity between 50% and 75% of board height. The bottom quarter remains unmodified for swipe detection; fine static dithering avoids a visible tonal boundary. Earth, its atmosphere and the controls retain their original brightness. Dense detail continues through the lower control area; there are no separately arranged rows of bright star decorations.

An occasional shooting star is rendered separately in code above the bottom quarter. Its warm-white head and thin tapered trail pass behind Earth; the original sky image and its cached texture remain stationary.

Generation prompt:

```text
Use case: stylized-concept. Create a brand-new high-quality astronomical Milky Way background texture for an interactive Earth globe app, inspired by the user's reference: an extraordinarily dense star field on deep indigo, with luminous blue-violet and softly pink galactic clouds, intricate dark dust lanes and countless fine white stars. Asset: opaque landscape 16:9 backdrop, ideally 3840 x 2160, rich crisp detail throughout. The Milky Way sweeps diagonally from upper left through a broad irregular luminous mass toward lower right, with branching wisps and organic star associations at many scales. Keep the entire bottom fifth busy with naturally distributed small sharp white and pale blue stars and varied clouds, including both lower corners; no empty black band. Varied star sizes but no huge flares or dominant beacon stars. Natural irregular distribution, mature premium space visual, not a regimented row or deliberate evenly spaced clusters. Darker indigo gaps should coexist with luminous detail, preserving enough contrast for a separately rendered Earth in the central area without a manufactured central blank spot. This image contains ONLY deep-space sky: no Earth, planets, moon, sun, horizon, landscape, objects, captions, labels, logos, borders, interface, watermarks or lens-flare streaks. Full bleed to every edge, opaque RGB/RGBA, no transparency.
```
