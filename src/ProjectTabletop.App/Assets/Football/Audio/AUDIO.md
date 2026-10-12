# Football sound effects

The stadium ambience, blue goal cheer and victory ovation use recordings
dedicated to the public domain under
[CC0 1.0 Universal](https://creativecommons.org/publicdomain/zero/1.0/).
The referee whistles by Pablo-F and red goal reaction by Quistard are under
[Creative Commons Attribution 3.0 Unported](https://creativecommons.org/licenses/by/3.0/).
The stadium chants are by Dan_AudioFile and avakas under
[Creative Commons Attribution 4.0 International](https://creativecommons.org/licenses/by/4.0/).
These licenses permit modification and redistribution, including commercial
use. Retain the corresponding attribution and license with each recording. Credits
are also included in the app's third-party notices and Licenses viewer.

| Bundled file | Duration | Source and changes |
| --- | ---: | --- |
| `start-whistle.mp3` | 0.820 s | [referee-whistle.wav](https://freesound.org/people/Pablo-F/sounds/90743/) by Pablo-F, CC BY 3.0. A real football referee whistle recorded with a SAMSON C01 microphone. Modified: excerpt from 0.060 to 0.880 s, level adjustment, 0.004 s fade-in and 0.045 s fade-out. |
| `end-whistle.mp3` | 1.720 s | The same Pablo-F recording, modified into two 0.240 s blasts at 0.000 and 0.380 s followed by a 0.960 s blast at 0.760 s. Uses the source from 0.060 to 1.020 s, level adjustment and short edge fades. |
| `goal-red.mp3` | 4.000 s | [Football Crowd - Goal- Cheer - Southampton Vs Hull at Saint Mary's Stadium.wav](https://freesound.org/people/Quistard/sounds/237678/) by Quistard, CC BY 3.0. Modified: excerpt from 3.700 to 7.700 s, preserving the previous edit's attack and level with an earlier 1.100 s fade-out to omit the later announcer section. |
| `goal-blue.mp3` | 5.800 s | [CRWDReac-SOCCER_Millerntor Stadium Crowd Reaction Goal 01_PHILIPP FEIT_FCSP 29.546_Sound Of Sankt Pauli](https://freesound.org/people/itmightgetloud/sounds/829455/) by Philipp Feit / itmightgetloud, CC0. A live goal reaction from FC St. Pauli's Millerntor Stadium. Excerpt from 1.800 to 7.600 s, short edge fades and loudness normalization. |
| `crowd-chant-1.mp3` | 8.000 s | [Football-match_Chanting-crowd_Ambience .stereo.wav](https://freesound.org/people/Dan_AudioFile/sounds/654084/) by Dan_AudioFile, CC BY 4.0. Live crowd chanting recorded in the upper stands at Wimbledon. Modified: excerpt from 0.000 to 8.000 s, 0.200 s fade-in, 1.200 s fade-out and loudness normalization. |
| `crowd-chant-2.mp3` | 8.000 s | [Football stadium fans sing after a near miss](https://freesound.org/people/avakas/sounds/263679/) by avakas, CC BY 4.0. A large crowd chanting at Aris F.C.'s stadium in Greece. Modified: excerpt from 20.000 to 28.000 s, 0.200 s fade-in, 1.200 s fade-out and loudness normalization. |
| `victory.mp3` | 7.600 s | [Crowd Cheer II](https://freesound.org/people/FoolBoyMedia/sounds/397435/) by FoolBoyMedia, CC0. Sports-win celebration with applause, whoops and whistles. Excerpt from 1.000 to 8.600 s, edge fades and loudness normalization. |
| `stadium-ambience.wav` | 30.000 s | [CRWDReac-SOCCER_Millerntor Stadium Crowd Reaction General Ambience 01_PHILIPP FEIT_FCSP 29.546_Sound Of Sankt Pauli](https://freesound.org/people/itmightgetloud/sounds/829454/) by Philipp Feit / itmightgetloud, CC0. General atmosphere recorded among 29,546 spectators at Millerntor Stadium. Excerpt from 286.000 to 319.000 s, with a three-second equal-power cyclic overlap and constant level adjustment. |

These are edits of recorded whistles and crowds. Both chants retain the
original stadium crowd and stereo recording, without layered solo voices.
Both whistles use a common source level normalized to -4.5 dBFS sample peak
before fades; no synthetic tones, pitch shifting or added reverb are used.
One-shot clips are bundled offline as 48 kHz stereo MP3 at 192 kbps. The
continuous stadium ambience uses 48 kHz stereo 16-bit PCM WAV so it has no
encoder padding at the loop boundary.
Each side has a fixed, distinct stadium recording. The red cheer fades earlier;
the blue cheer lasts 5.800 seconds. The chants use the blue goal cheer's loudness
target, while the shorter red cheer retains its previous attack level.
The victory recording contains no added instrument or synthetic crowd layer.

The ambience retains the natural crowd dynamics at -23.00 LUFS integrated,
-14.58 dBTP true peak and 4.2 LU loudness range. Its overlap blends the last
three seconds of the selected passage with the first three seconds, using
equal-power weights. The file boundary itself joins consecutive samples from
the source recording; there is no fade to silence. The last and first 250 ms
measure -27.66 and -27.86 dBFS RMS respectively. The finished WAV occupies
5,760,078 bytes (about 5.5 MiB). Loudness was measured after PCM encoding.

## Reproduction and provenance

Run `tools/prepare_football_audio.py --ffmpeg /path/to/ffmpeg` from the repository
with Python and NumPy installed. The script downloads the Freesound HQ preview
encodings, checks the source SHA-256 hashes, and performs the edits. Original
downloads stay in the ignored `artifacts/football-audio` folder.
Pass `--only goal-red goal-blue` to rebuild the goal effects without rewriting
the existing whistles, chants or victory recording.
Pass `--only goal-red crowd-chant-1 crowd-chant-2` to rebuild the shorter red
cheer and the two stadium chants without changing the other sounds.
Pass `--only start-whistle end-whistle` to rebuild only the referee whistles.
Pass `--only stadium-ambience` to rebuild just the background loop. The loop
uses one constant gain after blending, preserving waveform continuity rather
than applying time-varying processing across its boundary.

| Source | Download | SHA-256 |
| --- | --- | --- |
| Pablo-F, 90743 | [HQ MP3](https://cdn.freesound.org/previews/90/90743_1476665-hq.mp3) | `68b815d65b169d0d634d9bd31fd0b6fb5be6b5d66e1cf7046b23d574c3119d86` |
| Dan_AudioFile, 654084 | [HQ MP3](https://cdn.freesound.org/previews/654/654084_12932169-hq.mp3) | `5aae7b9a5261d54dee3e2b473ca0d17e4c135127e09fa0cf1b1ce42318517942` |
| avakas, 263679 | [HQ MP3](https://cdn.freesound.org/previews/263/263679_1467893-hq.mp3) | `9b890df3a85e141c726a0289964d945f81bec633489b4362c32ffa9818d51b1d` |
| Quistard, 237678 | [HQ MP3](https://cdn.freesound.org/previews/237/237678_2378802-hq.mp3) | `1851e61523968ae47bf7b46af1f5130b2c13e13592b6bd01f628f0e103db5d37` |
| Philipp Feit / itmightgetloud, 829455 | [HQ MP3](https://cdn.freesound.org/previews/829/829455_3625328-hq.mp3) | `0f5b0bf37d3c41b52fbfbba9400f3db904f94af50d68e385b2537db781fccdb2` |
| FoolBoyMedia, 397435 | [HQ MP3](https://cdn.freesound.org/previews/397/397435_4019029-hq.mp3) | `efe57f1e7dbed21e1561b8090cf54301a78775b4b8bf8ff418507c7e364f7f56` |
| Philipp Feit / itmightgetloud, 829454 | [HQ MP3](https://cdn.freesound.org/previews/829/829454_3625328-hq.mp3) | `00728388fe04840db4f8fd98e76d9140336bba313b27b82314d7a4951d27829c` |

See the included
`CC0-1.0.txt` for the public-domain dedication and fallback license, and
`CC-BY-3.0.txt` for the attribution license applying to the whistles and red goal cheer.
`CC-BY-4.0.txt` contains the attribution license applying to both stadium chants.
