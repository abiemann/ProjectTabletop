using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using ProjectTabletop.App.Projection;
using Windows.Graphics;
using Windows.System;

namespace ProjectTabletop.App;

public sealed partial class ProjectionWindow : Window
{
    private readonly SceneCompositor _scene;
    private Microsoft.UI.DisplayId? _targetDisplayId;
    private WindowId? _controlWindowId;
    private readonly SemaphoreSlim _placementOperation = new(1, 1);
    private readonly DisplayAreaWatcher _displayWatcher;
    private bool _closed;

    public ProjectionWindow(SceneCompositor scene)
    {
        _scene = scene;
        InitializeComponent();
        AppPalette.ApplyTitleBar(AppWindow.TitleBar);
        _displayWatcher = DisplayArea.CreateWatcher();
        _displayWatcher.Removed += (_, _) => QueuePlacementCheck();
        _displayWatcher.Updated += (_, _) => QueuePlacementCheck();
        _displayWatcher.Start();
        AppWindow.Changed += (_, args) =>
        {
            if (args.DidPositionChange || args.DidSizeChange) QueuePlacementCheck();
        };
        Closed += (_, _) =>
        {
            _closed = true;
            _displayWatcher.Stop();
        };
        Content.KeyDown += (_, args) =>
        {
            if (args.Key != VirtualKey.Escape) return;
            args.Handled = true;
            Close();
        };
    }

    public bool IsFullScreen => AppWindow.Presenter.Kind == AppWindowPresenterKind.FullScreen;

    public bool IsAlwaysOnTop => !_closed &&
        (GetWindowLongPtr(WinRT.Interop.WindowNative.GetWindowHandle(this), -20).ToInt64() & 0x8) != 0;

    public string? ActualDisplayId =>
        DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.None)?.DisplayId.Value
            .ToString(CultureInfo.InvariantCulture);

    public async Task ShowOnAsync(DisplayArea display, WindowId controlWindowId)
    {
        await _placementOperation.WaitAsync();
        try
        {
            ThrowIfClosed();
            AppWindow.Hide();
            _targetDisplayId = display.DisplayId;
            _controlWindowId = controlWindowId;
            await ApplyWindowModeAsync(fullScreen: true);
        }
        finally { _placementOperation.Release(); }
    }

    public async Task SetFullScreenAsync(bool enabled)
    {
        await _placementOperation.WaitAsync();
        try { await ApplyWindowModeAsync(enabled); }
        finally { _placementOperation.Release(); }
    }

    private async Task ApplyWindowModeAsync(bool fullScreen)
    {
        try
        {
            ThrowIfClosed();
            AppWindow.Hide();
            SetAlwaysOnTop(false);
            var target = ResolveTargetDisplay();
            AppWindow.SetPresenter(AppWindowPresenterKind.Default);
            await WaitUntilAsync(() => AppWindow.Presenter.Kind == AppWindowPresenterKind.Overlapped,
                "restore the output window", verifyDisplay: false);
            PositionOn(target);
            VerifyPlacement();

            // The first native Show can apply the startup windowed presenter.
            // Let it and XAML loading finish on the already verified projector,
            // then request fullscreen and wait for the actual presenter to settle.
            AppWindow.Show(false);
            await WaitUntilAsync(() => AppWindow.IsVisible && Content is FrameworkElement { IsLoaded: true },
                "initialize the output window");
            var requested = fullScreen ? AppWindowPresenterKind.FullScreen : AppWindowPresenterKind.Overlapped;
            AppWindow.SetPresenter(fullScreen ? AppWindowPresenterKind.FullScreen : AppWindowPresenterKind.Default);
            await WaitUntilAsync(() => AppWindow.IsVisible && AppWindow.Presenter.Kind == requested,
                fullScreen ? "enter fullscreen on the projector" : "restore windowed output");
            VerifyPlacement();
            // Keep ordinary laptop window edges behind the projector without
            // activating it or changing its verified bounds. Windowed output
            // returns to ordinary stacking order.
            SetAlwaysOnTop(fullScreen);
        }
        catch
        {
            if (!_closed) AppWindow.Hide();
            throw;
        }
    }

    private async Task WaitUntilAsync(Func<bool> ready, string operation, bool verifyDisplay = true)
    {
        var deadline = Stopwatch.StartNew();
        long? stableSince = null;
        while (deadline.Elapsed < TimeSpan.FromSeconds(5))
        {
            ThrowIfClosed();
            if (verifyDisplay) VerifyPlacement();
            if (ready())
            {
                stableSince ??= Stopwatch.GetTimestamp();
                if (Stopwatch.GetElapsedTime(stableSince.Value) >= TimeSpan.FromMilliseconds(150)) return;
            }
            else stableSince = null;
            // Yield to the UI message loop so native presenter/XAML transitions
            // can complete. No scan is allowed to start during this bounded wait.
            await Task.Delay(25);
        }
        throw new TimeoutException("Windows did not " + operation + ". Output was hidden; try opening it again.");
    }

    private void ThrowIfClosed()
    {
        if (_closed) throw new InvalidOperationException("The projection window was closed.");
    }

    private void QueuePlacementCheck() => DispatcherQueue.TryEnqueue(() =>
    {
        if (_closed || !AppWindow.IsVisible || _placementOperation.CurrentCount == 0) return;
        try { VerifyPlacement(); }
        catch (InvalidOperationException)
        {
            // Windows can move a window to the laptop when a display disappears.
            // Closing also invalidates board alignment through the normal handler.
            AppWindow.Hide();
            Close();
        }
    });

    private void SetAlwaysOnTop(bool enabled)
    {
        const uint noSizeMoveActivateOrOwnerOrder = 0x0001 | 0x0002 | 0x0010 | 0x0200;
        if (!SetWindowPos(WinRT.Interop.WindowNative.GetWindowHandle(this),
                new IntPtr(enabled ? -1 : -2), 0, 0, 0, 0, noSizeMoveActivateOrOwnerOrder))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not set the projector window stacking order.");
        if (IsAlwaysOnTop != enabled)
            throw new InvalidOperationException("Windows did not apply the projector window stacking order.");
    }

    [DllImport("user32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr window, IntPtr insertAfter,
        int x, int y, int width, int height, uint flags);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", ExactSpelling = true)]
    private static extern IntPtr GetWindowLongPtr(IntPtr window, int index);

    public void VerifyTargetDisplay() => VerifyPlacement();

    private DisplayArea ResolveTargetDisplay()
    {
        if (_targetDisplayId is not { } targetId || _controlWindowId is not { } controlId)
            throw new InvalidOperationException("Choose a projection display before opening output.");
        var target = DisplayArea.GetFromDisplayId(targetId) ??
            throw new InvalidOperationException("The selected projection display is no longer connected. Refresh displays.");
        var bounds = target.OuterBounds;
        if (bounds.Width <= 0 || bounds.Height <= 0)
            throw new InvalidOperationException("The selected projection display has no usable desktop bounds. Refresh displays and check Windows display settings.");
        var controls = DisplayArea.GetFromWindowId(controlId, DisplayAreaFallback.None) ??
            throw new InvalidOperationException("The control window's display could not be verified. Move the controls onto the laptop display.");
        if (controls.DisplayId.Value == target.DisplayId.Value)
            throw new InvalidOperationException("Choose a projector display separate from the control window. Use Extend in Windows display settings.");
        return target;
    }

    private void PositionOn(DisplayArea target)
    {
        var bounds = target.OuterBounds;
        if (bounds.Width <= 0 || bounds.Height <= 0)
            throw new InvalidOperationException("The selected projection display no longer has usable desktop bounds.");
        AppWindow.MoveAndResize(new RectInt32(0, 0, bounds.Width, bounds.Height), target);
    }

    private void VerifyPlacement()
    {
        var target = ResolveTargetDisplay();
        var actual = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.None);
        if (actual is null || actual.DisplayId.Value != target.DisplayId.Value)
            throw new InvalidOperationException("Windows could not place the output on the selected projector display. Output was hidden to keep the controls visible.");
    }

    private void OutputCanvas_Draw(ICanvasAnimatedControl sender, CanvasAnimatedDrawEventArgs args)
    {
        _scene.Draw(args.DrawingSession, (float)sender.Size.Width, (float)sender.Size.Height,
            preview: false, runningSlowly: args.Timing.IsRunningSlowly);
    }
}
