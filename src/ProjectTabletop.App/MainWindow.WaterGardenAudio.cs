using ProjectTabletop.App.Media;

namespace ProjectTabletop.App;

public sealed partial class MainWindow
{
    private readonly DisplaySoundPlayer _waterGardenSound = new("Water Garden fountain", .27);
    private readonly DisplaySoundPlayer _waterGardenSwishSound = new("Water Garden wand", .25);
    private static readonly DisplaySoundClip[] WaterGardenFountainSound =
        [new("fountain-loop", Path.Combine(AppContext.BaseDirectory, "Assets", "WaterGarden", "Audio", "fountain-loop.wav"))];
    private static readonly DisplaySoundClip[] WaterGardenSwishSound =
        [new("wand-swish-loop", Path.Combine(AppContext.BaseDirectory, "Assets", "WaterGarden", "Audio", "wand-swish-loop.wav"))];

    private void WaterGardenAudioChanged()
    {
        // Flow, contact and visibility changes can arrive under the compositor lock. Read the
        // current state on the dispatcher so queued starts cannot outlive exit.
        DispatcherQueue.TryEnqueue(UpdateWaterGardenAudio);
    }

    private void UpdateWaterGardenAudio()
    {
        if (!_closing && _scene.WaterGardenAudioActive)
            _waterGardenSound.EnsureLooping(WaterGardenFountainSound);
        else
            _waterGardenSound.Stop();
        if (!_closing && _scene.WaterGardenSwishAudioActive)
            _waterGardenSwishSound.EnsureLooping(WaterGardenSwishSound);
        else
            _waterGardenSwishSound.Stop();
    }
}
