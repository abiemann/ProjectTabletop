# Football sound effects

The whistles, chants, original goal cheer and victory ovation use recordings
dedicated to the public domain under
[CC0 1.0 Universal](https://creativecommons.org/publicdomain/zero/1.0/).
The two additional goal reactions are by Quistard under
[Creative Commons Attribution 3.0 Unported](https://creativecommons.org/licenses/by/3.0/).
These licenses permit modification and redistribution, including commercial
use. Retain the Quistard attribution and license with those recordings. Credits
are also included in the app's third-party notices and Licenses viewer.

| Bundled file | Duration | Source and changes |
| --- | ---: | --- |
| `start-whistle.mp3` | 0.536 s | [Referee whistle sound.wav](https://freesound.org/people/Rosa-Orenes256/sounds/538422/) by Rosa-Orenes256. Level adjusted; short edge fades. |
| `end-whistle.mp3` | 1.350 s | The same Rosa-Orenes256 whistle, edited into two short blasts and one longer blast. |
| `goal-cheer.mp3` | 5.800 s | [AF Crowd Cheer LOOP .wav](https://freesound.org/people/mglennsound/sounds/678544/) by mglennsound. Excerpt from the recorded generic sports crowd, level adjusted and faded. |
| `goal-cheer-2.mp3` | 5.800 s | [Football Crowd - Goal- Cheer - Southampton Vs Hull at Saint Mary's Stadium.wav](https://freesound.org/people/Quistard/sounds/237678/) by Quistard, CC BY 3.0. Modified: excerpt from 3.700 to 9.500 s, short edge fades and loudness normalization. |
| `goal-cheer-3.mp3` | 5.800 s | [Football Crowd - Goal Cheer - Southampton Vs Hull at Saint Mary's Stadium.wav](https://freesound.org/people/Quistard/sounds/237679/) by Quistard, CC BY 3.0. Modified: a different goal reaction from 0.850 to 6.650 s, short edge fades and loudness normalization. |
| `crowd-chant-1.mp3` | 6.300 s | [Hey - Men Shouting together - Jewish Wedding](https://freesound.org/people/khenshom/sounds/527740/) by khenshom, arranged into a neutral rhythmic “hey” chant over mglennsound's recorded crowd. Stereo alternation, quiet delayed voice tails, level adjustments and fades. |
| `crowd-chant-2.mp3` | 6.300 s | The same recorded voices and crowd, arranged into a second rhythm. |
| `victory.mp3` | 7.600 s | [Crowd Cheer II](https://freesound.org/people/FoolBoyMedia/sounds/397435/) by FoolBoyMedia, CC0. Sports-win celebration with applause, whoops and whistles. Excerpt from 1.000 to 8.600 s, edge fades and loudness normalization. |

These are edits of recorded whistles, voices and crowds. The chant uses
only the word “hey”; it does not include a named club or a borrowed song.
Finished clips are bundled offline as 48 kHz stereo MP3 at 192 kbps.
The two additional goal cheers are normalized to approximately the original
cheer's perceived level; the longer victory crowd is slightly louder. The
victory recording contains no added instrument or synthetic crowd layer.

## Reproduction and provenance

Run `tools/prepare_football_audio.py --ffmpeg /path/to/ffmpeg` from the repository
with Python and NumPy installed. The script downloads the Freesound HQ preview
encodings, checks the source SHA-256 hashes, and performs the edits. Original
downloads stay in the ignored `artifacts/football-audio` folder.
Pass `--only goal-cheer-2 goal-cheer-3 victory` to rebuild the new crowd effects
without rewriting the existing whistles, chants or original goal cheer.

| Source | Download | SHA-256 |
| --- | --- | --- |
| Rosa-Orenes256, 538422 | [HQ MP3](https://cdn.freesound.org/previews/538/538422_11966684-hq.mp3) | `1bc56409d3439a16fd54bc8fa83affb7e2bc69519b15ac49979d924e895910bf` |
| mglennsound, 678544 | [HQ MP3](https://cdn.freesound.org/previews/678/678544_14787385-hq.mp3) | `77765a7ec1439e63527e22d323e9699c97c8cd6ce010c310ef5843179f95f402` |
| khenshom, 527740 | [HQ MP3](https://cdn.freesound.org/previews/527/527740_6652872-hq.mp3) | `702b5a70275146eb75db7fe1ba0b81f247015ef29e5ebebb31a254f2e8d4e2c1` |
| Quistard, 237678 | [HQ MP3](https://cdn.freesound.org/previews/237/237678_2378802-hq.mp3) | `1851e61523968ae47bf7b46af1f5130b2c13e13592b6bd01f628f0e103db5d37` |
| Quistard, 237679 | [HQ MP3](https://cdn.freesound.org/previews/237/237679_2378802-hq.mp3) | `628a884062e85f7315a87be91947dc4ba5d5632285c60a4dbc17f9b360d6d70b` |
| FoolBoyMedia, 397435 | [HQ MP3](https://cdn.freesound.org/previews/397/397435_4019029-hq.mp3) | `efe57f1e7dbed21e1561b8090cf54301a78775b4b8bf8ff418507c7e364f7f56` |

Source pages and licenses checked on October 11, 2026. See the included
`CC0-1.0.txt` for the public-domain dedication and fallback license, and
`CC-BY-3.0.txt` for the attribution license applying to the additional goal cheers.
