using System.Diagnostics;
using Microsoft.Graphics.Canvas;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using ProjectTabletop.App.Camera;
using ProjectTabletop.App.Projection;
using ProjectTabletop.Calibration;
using ProjectTabletop.Vision;
using Windows.Graphics;

namespace ProjectTabletop.App;

public sealed partial class MainWindow : Window
{
    // Globe opens on the central meridian of this PC's standard time zone
    // (15 degrees per hour), unaffected by daylight saving.
    private readonly SceneCompositor _scene = new(globe: CreateHomeGlobe());
    private readonly CameraCaptureService _camera = new();
    private readonly object _visionGate = new();
    private VisionEngine _vision = new();
    private ProjectionWindow? _output;
    private string? _outputDisplayId;
    private readonly DispatcherQueueTimer _statusTimer;
    private bool _initialized;
    private bool _closing;
    private CameraFrame? _latestCameraFrame;
    private CameraFrame? _frozenFrame;
    private CameraFrame? _bitmapFrame;
    private CanvasBitmap? _cameraBitmap;
    private CanvasDevice? _cameraBitmapDevice;
    private IReadOnlyList<PieceDetection> _latestDetections = Array.Empty<PieceDetection>();
    private long _lastPreviewTick;
    private long _lastStatusTick = Stopwatch.GetTimestamp();
    private long _priorOutputFrames;
    private long _priorPreviewFrames;
    private long _priorOutputSlowFrames;
    private long _priorPreviewSlowFrames;
    private long _priorMediaRevision = -1;
    private long _priorFrameReadyEvents;
    private long _priorSurfaceCopies;
    private int _detecting;
    private string? _visionError;
    private string? _selectedCameraId;
    private readonly List<PixelPoint> _pieceOutline = [];
    private PixelPoint? _pieceFront;
    private readonly List<PixelPoint> _boardCameraPoints = [];
    private readonly List<PixelPoint> _topCameraPoints = [];
    private AnnotationMode _annotationMode = AnnotationMode.None;
    private CalibrationSession? _calibration;
    private readonly string _appDataDirectory = ResolveAppDataDirectory();

    private static string ResolveAppDataDirectory()
    {
        var configured = Environment.GetEnvironmentVariable("PROJECT_TABLETOP_DATA_DIR");
        return string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ProjectTabletop")
            : Path.GetFullPath(configured);
    }

    // Earth starts facing the local time zone's tz reference city; zones without
    // one fall back to their UTC offset's longitude on the equator.
    private static ProjectTabletop.Interaction.GlobeState CreateHomeGlobe()
    {
        var city = ProjectTabletop.Interaction.GlobeHome.ReferenceCity(TimeZoneInfo.Local,
            System.Globalization.RegionInfo.CurrentRegion.TwoLetterISORegionName);
        return city is { } home ? new(home.Longitude, home.Latitude)
            : new(Math.Clamp(TimeZoneInfo.Local.BaseUtcOffset.TotalHours * 15, -180, 180));
    }

    public MainWindow()
    {
        InitializeComponent();
        AppPalette.ApplyTitleBar(AppWindow.TitleBar);
        InitializeHandDetectionLogging();
        _camera.FrameReceived += Camera_FrameReceived;
        _camera.CaptureFailed += Camera_CaptureFailed;
        Closed += MainWindow_Closed;
        _statusTimer = DispatcherQueue.CreateTimer();
        _statusTimer.Interval = TimeSpan.FromSeconds(1);
        _statusTimer.Tick += StatusTimer_Tick;
        _statusTimer.Start();
        _initialized = true;
        StartHandTrackingStatus();
        RefreshDisplays();
        InitializeProjectionSettings();
        InitializeDisplayAudio();
        _ = RefreshCamerasAsync();
        UpdateTrainingStatus();
        SyncVisionSettingsControls();
        _ = TryLoadAutosavedVisionAsync();
        _ = InitializeMonopolySaveAsync();
        _ = WarmGlobeResourcesAsync();
        StartControlHost();
    }

    private sealed record CameraChoice(CameraDeviceInfo Device)
    {
        public override string ToString() => Device.DisplayName;
    }

    private sealed record DisplayChoice(DisplayArea Area, int Number)
    {
        // DisplayArea objects can outlive a display-topology change. Keep the
        // picker coherent and resolve the ID again immediately before output.
        public Microsoft.UI.DisplayId DisplayId { get; } = Area.DisplayId;
        public string Id { get; } = Area.DisplayId.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        public RectInt32 Bounds { get; } = Area.OuterBounds;
        public bool IsPrimary { get; } = Area.IsPrimary;
        public DisplayModeInfo? PhysicalMode { get; } = DisplayModeInfo.ForArea(Area);
        // Desktop bounds place AppWindow. XAML canvas sizes use separate DIPs.
        public int Width => Bounds.Width;
        public int Height => Bounds.Height;
        public override string ToString() =>
            $"{PhysicalMode?.FriendlyName ?? $"Display {Number}"}: " + (PhysicalMode is { } mode
                ? $"{mode.Width} × {mode.Height} @ {mode.RefreshHertz} Hz physical"
                : "physical mode unavailable") +
            $" ({Width} × {Height} desktop)" +
            (IsPrimary ? " (primary)" : " (secondary)");
    }

    private enum AnnotationMode { None, PieceOutline, PieceFront, BoardCalibration, TopCalibration }

    private DisplayChoice? SelectedDisplay => DisplayComboBox.SelectedItem as DisplayChoice;
    private CameraChoice? SelectedCamera => CameraComboBox.SelectedItem as CameraChoice;

    private void SetStatus(string message) => StatusText.Text = message;

    private void RefreshDisplays_Click(object sender, RoutedEventArgs e) => RefreshDisplays();

    private void RefreshDisplays()
    {
        var previousId = SelectedDisplay?.Id;
        // Indexing avoids a Windows App SDK WinRT collection enumerator failure seen on
        // some framework/runtime combinations when FindAll() is fed into LINQ.
        var areas = DisplayArea.FindAll();
        var displays = new DisplayChoice[areas.Count];
        for (var index = 0; index < areas.Count; index++)
            displays[index] = new DisplayChoice(areas[index], index + 1);
        var controlDisplay = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.None);
        DisplayComboBox.Items.Clear();
        foreach (var display in displays) DisplayComboBox.Items.Add(display);
        DisplayComboBox.SelectedItem = displays.FirstOrDefault(item => item.Id == previousId)
            ?? displays.FirstOrDefault(item => item.Width > 0 && item.Height > 0 &&
                (controlDisplay is not null ? item.DisplayId.Value != controlDisplay.DisplayId.Value : !item.IsPrimary));
        if (SelectedDisplay is null)
            SetStatus("Connect a separate projector display and use Windows Extend mode, then refresh displays.");
    }

    private DisplayChoice ResolveProjectionDisplay()
    {
        var selected = SelectedDisplay ?? throw new InvalidOperationException(
            "Select a separate projector display. Use Windows Extend mode, then refresh displays.");
        var area = DisplayArea.GetFromDisplayId(selected.DisplayId);
        if (area is null || area.DisplayId.Value != selected.DisplayId.Value)
            throw new InvalidOperationException("The selected projector is no longer connected. Refresh displays and select it again.");
        var current = new DisplayChoice(area, selected.Number);
        if (current.Width <= 0 || current.Height <= 0)
            throw new InvalidOperationException("The projector reports an empty display area. Check its connection, use Windows Extend mode, then refresh displays.");
        var controls = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.None);
        if (controls is null || controls.DisplayId.Value == current.DisplayId.Value)
            throw new InvalidOperationException("Choose a projector display separate from the laptop controls. Output was not opened over the controls.");
        if (current.Bounds.X != selected.Bounds.X || current.Bounds.Y != selected.Bounds.Y ||
            current.Width != selected.Width || current.Height != selected.Height)
        {
            var index = DisplayComboBox.Items.IndexOf(selected);
            if (index >= 0) DisplayComboBox.Items[index] = current;
            DisplayComboBox.SelectedItem = current;
        }
        return current;
    }

    private void DisplayComboBox_SelectionChanged(object sender,
        Microsoft.UI.Xaml.Controls.SelectionChangedEventArgs e)
    {
        if (!_initialized) return;
        if (SelectedDisplay is not { } display)
        {
            _scene.ClearBoardMediaClip();
            ProjectionSettingsDisplayChanged();
            return;
        }
        if (Volatile.Read(ref _boardSetupActive)) EndBoardSetup();
        ClearHandTracking();
        _scene.ClearBoardMediaClip();
        _scene.SetDisplayAspect((double)display.Width / display.Height);
        InvalidateCalibration("Projection display changed. Recalibrate both planes.");
        ProjectionSettingsDisplayChanged();
        SetStatus($"Selected {display}. " + (_output is null || _outputDisplayId == display.Id
            ? "Calibration follows the fullscreen canvas layout size."
            : "Press Open output to move the projector window before calibrating."));
    }

    private void StatusTimer_Tick(DispatcherQueueTimer sender, object args)
    {
        var (outputFrames, previewFrames, outputSlow, previewSlow) = _scene.FrameCounts;
        var now = Stopwatch.GetTimestamp();
        var elapsed = Stopwatch.GetElapsedTime(_lastStatusTick, now).TotalSeconds;
        var outputFps = elapsed > 0 ? (outputFrames - _priorOutputFrames) / elapsed : 0;
        var previewFps = elapsed > 0 ? (previewFrames - _priorPreviewFrames) / elapsed : 0;
        var outputSlowDelta = outputSlow - _priorOutputSlowFrames;
        var previewSlowDelta = previewSlow - _priorPreviewSlowFrames;
        var (mediaRevision, videoAssets, frameReadyEvents, surfaceCopies) = _scene.PlaybackCounts;
        var sameMedia = mediaRevision == _priorMediaRevision;
        var eventRate = sameMedia && elapsed > 0
            ? Math.Max(0, frameReadyEvents - _priorFrameReadyEvents) / elapsed : 0;
        var copyRate = sameMedia && elapsed > 0
            ? Math.Max(0, surfaceCopies - _priorSurfaceCopies) / elapsed : 0;
        _lastStatusTick = now;
        _priorOutputFrames = outputFrames;
        _priorPreviewFrames = previewFrames;
        _priorOutputSlowFrames = outputSlow;
        _priorPreviewSlowFrames = previewSlow;
        _priorMediaRevision = mediaRevision;
        _priorFrameReadyEvents = frameReadyEvents;
        _priorSurfaceCopies = surfaceCopies;
        var mediaError = _scene.MediaError;
        if (mediaError is not null) SetStatus(mediaError);
        else if (_visionError is not null) SetStatus(_visionError);
        UpdateCameraHealth();
        UpdateBoardSetupStatus();
        _scene.TickMonopoly(ProjectTabletop.Interaction.MonotonicClock.UtcNow);
        QueueMonopolySave();
        UpdateBoardAppStatus();
        UpdateBoardSizeEstimate();
        RenderStatusText.Text = $"Draw callbacks: {outputFps:F1}/s output ({outputSlowDelta} slow), " +
            $"{previewFps:F1}/s preview ({previewSlowDelta} slow). " +
            (videoAssets == 0 ? "No video assets loaded. " :
                $"Video ({videoAssets} loaded asset{(videoAssets == 1 ? "" : "s")}): " +
                $"frame-ready events {frameReadyEvents} total ({eventRate:F1}/s), " +
                $"GPU surface copies {surfaceCopies} total ({copyRate:F1}/s). ") +
            "Slow is Win2D timing; these counters do not measure displayed or dropped frames.";
    }

    private async void MainWindow_Closed(object sender, WindowEventArgs args)
    {
        if (_closing) return;
        _closing = true;
        DisposeDisplayAudio();
        _photocopierSound.Dispose();
        _statusTimer.Stop();
        await DisposeHandTrackingAsync();
        if (_monopolySaveTask is not null) await _monopolySaveTask;
        if (_controlHost is not null) await _controlHost.DisposeAsync();
        _output?.Close();
        _camera.FrameReceived -= Camera_FrameReceived;
        _camera.CaptureFailed -= Camera_CaptureFailed;
        await _camera.DisposeAsync();
        _cameraBitmap?.Dispose();
        _scene.Dispose();
        lock (_visionGate) _vision.Dispose();
    }
}
