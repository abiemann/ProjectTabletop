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

namespace ProjectTabletop.App;

public sealed partial class MainWindow : Window
{
    private readonly SceneCompositor _scene = new();
    private readonly CameraCaptureService _camera = new();
    private readonly object _visionGate = new();
    private VisionEngine _vision = new();
    private ProjectionWindow? _output;
    private string? _outputDisplayId;
    private readonly DispatcherQueueTimer _statusTimer;
    private bool _initialized;
    private bool _updatingStage;
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

    public MainWindow()
    {
        InitializeComponent();
        _camera.FrameReceived += Camera_FrameReceived;
        _camera.CaptureFailed += Camera_CaptureFailed;
        Closed += MainWindow_Closed;
        _statusTimer = DispatcherQueue.CreateTimer();
        _statusTimer.Interval = TimeSpan.FromSeconds(1);
        _statusTimer.Tick += StatusTimer_Tick;
        _statusTimer.Start();
        _initialized = true;
        RefreshDisplays();
        _ = RefreshCamerasAsync();
        UpdateStage();
        UpdateTrainingStatus();
        SyncVisionSettingsControls();
        _ = TryLoadAutosavedVisionAsync();
    }

    private sealed record CameraChoice(CameraDeviceInfo Device)
    {
        public override string ToString() => Device.DisplayName;
    }

    private sealed record DisplayChoice(DisplayArea Area, int Number)
    {
        public string Id => Area.DisplayId.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        public DisplayModeInfo? PhysicalMode { get; } = DisplayModeInfo.ForArea(Area);
        // Layout dimensions are used by AppWindow and fullscreen Win2D canvas mapping.
        public int Width => Area.OuterBounds.Width;
        public int Height => Area.OuterBounds.Height;
        public override string ToString() =>
            $"Display {Number}: " + (PhysicalMode is { } mode
                ? $"{mode.Width} × {mode.Height} @ {mode.RefreshHertz} Hz physical"
                : "physical mode unavailable") +
            $" ({Width} × {Height} layout)" +
            (Area.IsPrimary ? " (laptop / primary)" : " (secondary)");
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
        DisplayComboBox.Items.Clear();
        foreach (var display in displays) DisplayComboBox.Items.Add(display);
        DisplayComboBox.SelectedItem = displays.FirstOrDefault(item => item.Id == previousId)
            ?? displays.FirstOrDefault(item => !item.Area.IsPrimary)
            ?? displays.FirstOrDefault();
        if (displays.Length == 0) SetStatus("Windows reports no projection display. Check the HDMI connection.");
    }

    private void DisplayComboBox_SelectionChanged(object sender,
        Microsoft.UI.Xaml.Controls.SelectionChangedEventArgs e)
    {
        if (!_initialized || SelectedDisplay is not { } display) return;
        _scene.SetDisplayAspect((double)display.Width / display.Height);
        UpdateStage();
        InvalidateCalibration("Projection display changed. Recalibrate both planes.");
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
        UpdateBoardSetupStatus();
        StageStatusText.Text = $"Stage: {StageSizeSlider.Value:P0} of display height; " +
            $"draw callbacks: {outputFps:F1}/s output ({outputSlowDelta} slow), " +
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
        _statusTimer.Stop();
        _output?.Close();
        _camera.FrameReceived -= Camera_FrameReceived;
        _camera.CaptureFailed -= Camera_CaptureFailed;
        await _camera.DisposeAsync();
        _cameraBitmap?.Dispose();
        _scene.Dispose();
        lock (_visionGate) _vision.Dispose();
    }
}
