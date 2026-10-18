# Globe Earth models

The three models share the same Earth geometry, geographic orientation, native-pixel edge smoothing and blue atmosphere. The independent NASA star field is unchanged.

- **CLOUDS** uses the existing NASA Blue Marble `earth-day-8192.png` and `earth-clouds-2048.jpg`, with the existing daylight and cloud-shadow rendering.
- **NO CLOUDS** uses only `earth-day-8192.png`, without loading or retaining the cloud bitmap.
- **NIGHT** uses only `earth-night-8192.png`, the NASA Black Marble 2016 colour composite. Its city lights are emissive, so the daylight shading does not extinguish them. The image is a global satellite composite, not a simultaneous photograph or live weather.

## Black Marble source and preparation

Source page: [Earth at Night / Black Marble: Flat Maps](https://science.nasa.gov/earth/earth-observatory/earth-at-night/maps/).

Credit: **NASA Earth Observatory images by Joshua Stevens, using Suomi NPP VIIRS data from Miguel Román, NASA GSFC.** NASA's [media usage guidelines](https://www.nasa.gov/nasa-brand-center/images-and-media/) apply. NASA does not endorse this application.

- Original: [BlackMarble_2016_3km.jpg](https://assets.science.nasa.gov/content/dam/science/esd/eo/images/imagerecords/144000/144898/BlackMarble_2016_3km.jpg), 13500 × 6750 opaque RGB, global equirectangular colour composite.
- Original SHA-256: `230aac448ae68c358be433dd518888cccb3a85ccf66f7b44326441c324ad6725`.
- Bundled `earth-night-8192.png`: 8192 × 4096 opaque RGB, 13,325,564 bytes.
- Bundled SHA-256: `0aeacbfa458d1b9646db3940aa1502a3067a62a9e15843b6e1268ff53d1d781d`.

`tools/prepare_globe_nasa_night.py` verifies the source checksum and dimensions, then resamples once with Pillow's Lanczos filter to the same 8K texture dimensions as the daytime Earth. It does not alter exposure, colours or geographic coverage, and adds no generated detail. The PNG avoids further lossy recompression. The original JPEG is a preparation artifact, not an additional bundled texture. Pillow is a preparation dependency only.

## Loading and memory

Only the selected Earth model is decoded. Merely opening MODEL does not load the other models. Each choice releases the previous model's effect bindings and bitmaps before asynchronously loading the new selection. CLOUDS holds one 128 MiB surface and one 8 MiB cloud bitmap; NO CLOUDS and NIGHT each hold one 128 MiB surface, based on decoded RGBA8 dimensions. The four existing sky tiles remain shared and cached separately.

One loader services the latest requested model, without a queue of parallel uploads. A newer selection invalidates an older completion, and disposal releases any pending completion instead of installing it into a replaced renderer. During a load, the star field continues to render and the board can overlay its loading message. This avoids a full-board black flash affecting the swipe reference. Revisiting a model reloads its files; the app does not retain a collection of inactive models or use the network at runtime. A failed model can be explicitly retried by selecting its button again; ordinary rendering never starts an automatic retry loop.

## Validation

The source and bundled PNG dimensions, colour mode and SHA-256 checksums have been verified. Native 4K GPU checks passed for all three models, actual corrupted-caption holds, one resident model, unchanged same-clock sky pixels, latest-choice loading races, and disposal during loading. Separate rail checks passed across landscape, portrait and perspective layouts, including simultaneous input, stale-frame rejection and rounded spotlight protection.

The user confirmed smooth zoom and all three models on the calibrated Facecam/projector setup, followed by drawer close/reopen, swipe hide/restore and EXIT. A 1,886-sample live recording covered the full 0.6x-to-3x zoom range and all three models, with no camera health warnings and at most one resident Earth model. This validates the tested setup; it is not a claim about every camera or projector.
