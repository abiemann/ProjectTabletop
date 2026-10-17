using ProjectTabletop.Vision;

namespace ProjectTabletop.App;

public sealed partial class MainWindow
{
    private string AutoProfileDirectory => Path.Combine(_appDataDirectory, "VisionAutosave");

    // Existing physical-piece profiles remain usable without their former
    // training panel. The VisionEngine still owns recognition and persistence.
    private async Task TryLoadAutosavedVisionAsync()
    {
        if (!File.Exists(Path.Combine(AutoProfileDirectory, "vision-profile.json"))) return;
        VisionEngine? loaded = null;
        try
        {
            var initial = _vision;
            loaded = await Task.Run(() => VisionEngine.Load(AutoProfileDirectory));
            lock (_visionGate)
            {
                if (_closing || !ReferenceEquals(_vision, initial) || _vision.Captures.Count != 0)
                {
                    loaded.Dispose();
                    return;
                }
                _vision = loaded;
                initial.Dispose();
            }
            loaded = null;
            SetStatus("Loaded the saved vision profile from " + AutoProfileDirectory);
        }
        catch (Exception ex)
        {
            loaded?.Dispose();
            if (!_closing) SetStatus("Could not load saved vision profile: " + ex.Message);
        }
    }
}
