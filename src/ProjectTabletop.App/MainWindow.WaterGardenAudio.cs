using ProjectTabletop.App.Media;

namespace ProjectTabletop.App;

public sealed partial class MainWindow
{
    private readonly DisplaySoundPlayer _waterGardenSound = new("Water Garden fountain", .45);
    private static readonly DisplaySoundClip[] WaterGardenFountainSound =
        [new("fountain-loop", Path.Combine(AppContext.BaseDirectory, "Assets", "WaterGarden", "Audio", "fountain-loop.wav"))];

    private void WaterGardenAudioChanged()
    {
        // Flow and visibility changes can arrive under the compositor lock. Read the
        // current state on the dispatcher so queued starts cannot outlive exit.
        DispatcherQueue.TryEnqueue(UpdateWaterGardenAudio);
    }

    private void UpdateWaterGardenAudio()
    {
        if (!_closing && _scene.WaterGardenAudioActive)
            _waterGardenSound.EnsureLooping(WaterGardenFountainSound);
        else
            _waterGardenSound.Stop();
    }
}
