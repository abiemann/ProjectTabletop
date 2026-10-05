# Crown & Deed artwork

`crown-deed-city.png` is original artwork created with the built-in imagegen tool on 2026-10-04 for this project. No reference image was supplied. The boulevard, district parcels, captions, and developing buildings are rendered separately in code; the player pieces use the original silver artwork documented below.

SHA-256: `71670bfaafdc36a2342e7800d058f87a676a9fa9ccda05d51270df8bdcce9919`.

## Generation prompt

Use case: stylized-concept
Asset type: original game-board environment artwork for Crown & Deed, a fictional merchant-city property game.
Create a gorgeous highly detailed premium painted/3D hybrid aerial map of a fictional prosperous European-inspired merchant city at blue hour. Strict overhead bird's-eye view, a square image. Around the exterior perimeter: finely modelled slate-roof townhouses, gilded domes, ivory stone civic architecture, courtyards, tiny warm windows, emerald gardens, two small canals, bridges and lamplit streets. The central 70 percent is an intentional large empty oval dark emerald ceremonial courtyard of polished stone with restrained engraved gold details, very subdued so readable game controls can be added in code. Architectural details concentrate near the outside perimeter and corners. A single broad oval boulevard encircles the courtyard. Keep the roadway unmarked, with no game tiles, no lettering, no numbers, no brands, no dice, no tokens, no cards, no logos. Oval is centered, extends approximately from x8% to92%, y7% to93%. Natural precise materials: engraved brass, granite paving, emerald patina, slate, warm glass. Rich mature elegant fantasy city, high-end game illustration, clear crisp silhouettes and beautiful fine detail, restrained cinematic warm light against blue/green shadow. No artificial rectangular opacity masks or blurred edges. No text whatsoever. Entire image opaque.

## Authored composition

The renderer places forty equal parcels around an oval boulevard, uses a new fictional district order, and builds lit architectural meshes over the city illustration. The image contains no brands, property labels, game tiles, or player tokens. It is intentionally opaque and is copied into build and publish output.

Canal animation uses a code-authored shader and water-only outlines traced from this same image, with stationary cutouts for boats, bridges and shoreline details. Refraction and warm lamp reflections render beneath the board foreground; the original image pixels and SHA-256 remain unchanged.

Architectural window animation also uses a code-authored shader with glass apertures traced from the original painting. Roughly 60% of the traced windows receive independent lighting schedules; panes in the same window share a schedule, and frames, mullions and street lamps stay fixed. Lit windows retain their painted texture, fading into dark moonlit glass when the room goes dark. No replacement bitmap or external artwork is used.

Eight original silver player pieces are documented in [Pieces/ARTWORK.md](Pieces/ARTWORK.md), including their generation prompts, PNG hashes, framing and verified transparency. The renderer preserves their aspect and silver material, aims each piece's forward axis toward the physical board centre, and adds a separate contact shadow and small player-colour marker. The artwork is loaded once per graphics device and shared by board pieces, roster portraits and setup previews.
