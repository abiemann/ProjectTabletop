#if DEBUG
using System.Globalization;
using Microsoft.UI.Windowing;
using ProjectTabletop.App.Projection;

namespace ProjectTabletop.App;

public sealed partial class MainWindow
{
    // Opens separate black test windows; never changes the current board or output.
    private async Task<object> VerifyProjectionWindowAsync()
    {
        var controls = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.None)
            ?? throw new InvalidOperationException("The controls' display could not be verified.");
        var target = ResolveProjectionDisplay().Area;
        var targetId = target.DisplayId.Value.ToString(CultureInfo.InvariantCulture);
        using var scene = new SceneCompositor();
        scene.SetBlackOutput(true);
        var output = new ProjectionWindow(scene);
        try
        {
            await output.ShowOnAsync(target, AppWindow.Id);
            VerifyState(fullScreen: true);
            await output.SetFullScreenAsync(false);
            VerifyState(fullScreen: false);
            await output.SetFullScreenAsync(true);
            VerifyState(fullScreen: true);
        }
        finally { output.Close(); }

        var rejectedOutput = new ProjectionWindow(scene);
        try
        {
            var rejected = false;
            try { await rejectedOutput.ShowOnAsync(controls, AppWindow.Id); }
            catch (InvalidOperationException) { rejected = true; }
            if (!rejected || rejectedOutput.AppWindow.IsVisible || rejectedOutput.IsAlwaysOnTop)
                throw new InvalidOperationException("Output targeting the laptop was not safely rejected.");
            VerifyControls();
        }
        finally { rejectedOutput.Close(); }

        return new { passed = true, fullScreenAlwaysOnTop = true, windowedNotAlwaysOnTop = true,
            fullScreenReentry = true, controlDisplayUnchanged = true, laptopTargetRejected = true };

        void VerifyState(bool fullScreen)
        {
            if (!output.AppWindow.IsVisible || output.IsFullScreen != fullScreen ||
                output.IsAlwaysOnTop != fullScreen || output.ActualDisplayId != targetId)
                throw new InvalidOperationException("The projection window's mode, stacking, or display was incorrect.");
            VerifyControls();
        }

        void VerifyControls()
        {
            if (DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.None)?.DisplayId.Value !=
                controls.DisplayId.Value)
                throw new InvalidOperationException("Projection placement changed the controls' display.");
        }
    }
}
#endif
