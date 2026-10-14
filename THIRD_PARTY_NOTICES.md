# Third-party notices

ProjectTabletop's original material is governed by the [project license](LICENSE).
The commercial-use restriction in that license does not replace or restrict the
separate licenses for third-party material.

The sections below cover bundled Globe imagery and adapted GPU rendering code.
Windows release packages also contain `ThirdPartyNotices/` and
`dependency-notices.json`, generated from the exact published NuGet dependencies,
their embedded license files, and reviewed upstream notice texts in
[`packaging/licenses`](https://github.com/abiemann/ProjectTabletop/blob/main/packaging/licenses/README.md). These include the .NET and
Visual C++ runtimes, Windows App SDK components, OpenCvSharp's native dependencies,
and the MCP helper's dependencies. The hand models retain their licenses beside
the models. Preserve all applicable licenses and attributions when redistributing.

## NASA Blue Marble imagery

Globe bundles the unmodified 8192 × 4096 land/ocean/sea-ice texture and
2048 × 1024 cloud composite from NASA's Blue Marble collection. Image credit:
NASA Goddard Space Flight Center, Reto Stöckli; visualization by Robert Simmon;
based on MODIS Science Team data, with USGS and NOAA surface/topography data.
The satellite composites describe 2001 observations, not current weather.
The pregenerated Globe menu thumbnail is rendered from the same imagery and
retains these credits.

Source: [NASA's The Blue Marble](https://science.nasa.gov/earth/earth-observatory/the-blue-marble-2181/).
Original [surface PNG](https://eoimages.gsfc.nasa.gov/images/imagerecords/57000/57730/land_ocean_ice_8192.png)
and [cloud JPEG](https://eoimages.gsfc.nasa.gov/images/imagerecords/57000/57747/cloud_combined_2048.jpg).
NASA imagery is used under its [media usage guidelines](https://www.nasa.gov/nasa-brand-center/images-and-media/).
NASA does not endorse this application.

## Fluid Paint by David Li

Source: [dli/paint](https://github.com/dli/paint), commit
[`6b8adfa226ecda14546c90b14a3d2a0d4898ad89`](https://github.com/dli/paint/tree/6b8adfa226ecda14546c90b14a3d2a0d4898ad89).

ProjectTabletop adapts the GPU fluid passes and paint-surface shading from
`simulator.js` and the upstream `shaders` directory into its C#/Direct2D renderer.
The brush interface is replaced with disturbance-triggered drops. The original
WebGL application is not bundled or executed.

```text
The MIT License (MIT)

Copyright (c) 2017 David Li (http://david.li)

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```

## WebGL Water by Evan Wallace

Source: [evanw/webgl-water](https://github.com/evanw/webgl-water), commit
[`73eda8be832b649367b25ea5690c1f0181bb56ad`](https://github.com/evanw/webgl-water/tree/73eda8be832b649367b25ea5690c1f0181bb56ad).
The upstream [`water.js`](https://github.com/evanw/webgl-water/blob/73eda8be832b649367b25ea5690c1f0181bb56ad/water.js)
and [`renderer.js`](https://github.com/evanw/webgl-water/blob/73eda8be832b649367b25ea5690c1f0181bb56ad/renderer.js)
headers identify Evan Wallace's 2011 copyright and MIT license.

Water Garden adapts the height-field wave update, disturbance and surface-normal
approach into native C#/Direct2D GPU shaders. Its basin, environment and water
shading are created for ProjectTabletop, with approximate caustic lighting rather
than the upstream refracted-mesh caustics renderer. The upstream WebGL application,
JavaScript runtime, pool-tile image and skybox images are not bundled.

The garden's moss-rock cluster, warm limestone and wet slate materials are original artwork generated for ProjectTabletop
with OpenAI's built-in image-generation tool. Their prompts and provenance are in
[`ARTWORK.md`](https://github.com/abiemann/ProjectTabletop/blob/main/src/ProjectTabletop.App/Assets/WaterGarden/ARTWORK.md).
They do not use pixels or branding from the reference garden imagery or upstream
WebGL Water imagery.

```text
MIT License

Copyright 2011 Evan Wallace

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```

## Football sound recordings

The bundled Football sound effects use the following recordings. These sources
are dedicated to the public domain under
[CC0 1.0 Universal](https://creativecommons.org/publicdomain/zero/1.0/):

- Blue-side goal cheer: [Philipp Feit (itmightgetloud), Millerntor Stadium Crowd Reaction Goal 01](https://freesound.org/people/itmightgetloud/sounds/829455/).
- Continuous stadium ambience: [Philipp Feit (itmightgetloud), Millerntor Stadium Crowd Reaction General Ambience 01](https://freesound.org/people/itmightgetloud/sounds/829454/).
- Victory celebration: [FoolBoyMedia, Crowd Cheer II](https://freesound.org/people/FoolBoyMedia/sounds/397435/).

The red-side goal cheer is by Quistard, recorded at Southampton vs Hull
at Saint Mary's Stadium: [Football Crowd - Goal- Cheer](https://freesound.org/people/Quistard/sounds/237678/).
It is licensed under [Creative Commons Attribution 3.0 Unported](https://creativecommons.org/licenses/by/3.0/).

The referee whistles use [Pablo-F, referee-whistle.wav](https://freesound.org/people/Pablo-F/sounds/90743/),
also licensed under [Creative Commons Attribution 3.0 Unported](https://creativecommons.org/licenses/by/3.0/).
The recording is trimmed, level-adjusted and arranged into kickoff and final-whistle cues.

The two stadium chants are licensed under
[Creative Commons Attribution 4.0 International](https://creativecommons.org/licenses/by/4.0/):

- [Dan_AudioFile, Football-match_Chanting-crowd_Ambience .stereo.wav](https://freesound.org/people/Dan_AudioFile/sounds/654084/), recorded at Wimbledon.
- [avakas, Football stadium fans sing after a near miss](https://freesound.org/people/avakas/sounds/263679/), recorded at Aris F.C.'s stadium.

ProjectTabletop trims, arranges, mixes and adjusts the levels of these recordings.
Full editing details, source checksums and all three license texts are bundled in
`Assets/Football/Audio` and available through **Settings → Licenses**.
See the [Football audio credits](https://github.com/abiemann/ProjectTabletop/blob/main/src/ProjectTabletop.App/Assets/Football/Audio/AUDIO.md).
These source recordings retain their respective terms independently of the project's
license.

## Water Garden recordings

The bundled fountain loop uses
[Trickling Water 3 by Yoyodaman234](https://freesound.org/people/Yoyodaman234/sounds/349072/),
dedicated to the public domain under
[CC0 1.0 Universal](https://creativecommons.org/publicdomain/zero/1.0/).
ProjectTabletop restores its previously prepared 32-second stereo water-only
loop and plays it at 27% volume. Restoration details, source and output
checksums, and the CC0 license text are bundled in `Assets/WaterGarden/Audio`
and available through **Settings → Licenses**.

The wand interaction loop also uses **Trickling Water 3 by Yoyodaman234**, under
**CC0 1.0 Universal**. ProjectTabletop selects a separate quiet excerpt from its
verified prepared fountain recording, filters low rumble and high frequencies,
crossfades the repeat boundary, smooths its edges and adjusts the level into an
8-second mono loop. It plays at 25% volume while the wand interacts with the water.
See the [Water Garden audio credits](https://github.com/abiemann/ProjectTabletop/blob/main/src/ProjectTabletop.App/Assets/WaterGarden/Audio/AUDIO.md)
for both loops, preparation details, checksums and the bundled CC0 license.

## ComputeSharp

The Paint, Globe and Water Garden GPU shaders use [ComputeSharp](https://github.com/Sergio0694/ComputeSharp)
3.2.0 through `ComputeSharp.D2D1.WinUI` and its `ComputeSharp.D2D1` dependency.
Their upstream revision is
[`9a7c9e0c755bf68447f7293e5729547750fe6be3`](https://github.com/Sergio0694/ComputeSharp/tree/9a7c9e0c755bf68447f7293e5729547750fe6be3).

```text
MIT License

Copyright (c) 2024 Sergio Pedri

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```
