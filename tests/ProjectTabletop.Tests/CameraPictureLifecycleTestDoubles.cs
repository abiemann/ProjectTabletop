using System.Collections.Concurrent;
using ProjectTabletop.App.Camera;
using ProjectTabletop.Interaction;

// Test-only types for compiling the actual coordinator without WinUI or a camera.
namespace Microsoft.UI.Xaml
{
    public sealed class RoutedEventArgs : EventArgs;
}

namespace ProjectTabletop.App.Camera
{
    public sealed class CameraCaptureService
    {
        public sealed record CameraControlInfo(string Api, string Unit, string Status, bool? Supported = null,
            double? Minimum = null, double? Maximum = null, double? Step = null, double? Value = null,
            bool? Automatic = null, bool? AutoSupported = null, string? Preset = null, string? Error = null);
        public sealed record CameraControlsSnapshot(bool CameraRunning, long Epoch, DateTimeOffset QueriedUtc,
            string Status, IReadOnlyDictionary<string, CameraControlInfo> Controls, string? Error = null);
        public sealed record CameraImageValues(double? Brightness, double? Contrast);
        public sealed record CameraImageControlResult(string Status, long Epoch, bool Changed,
            CameraImageValues Before, CameraImageValues After, bool RollbackAttempted = false,
            bool RollbackSucceeded = false, string? Error = null);
        public sealed record WriteRequest(string Device, long Epoch, double? Brightness, double? Contrast,
            CancellationToken Cancellation);

        public bool IsRunning = true;
        public string? ActiveDeviceId = "test-camera";
        public long Epoch = 5;
        public double Brightness = 50, Contrast = 116;
        public int ControlQueries;
        public readonly ConcurrentQueue<WriteRequest> Calls = new();
        public Func<WriteRequest, Task<CameraImageControlResult>>? Write;

        public Task<CameraControlsSnapshot> GetControlsAsync()
        {
            ControlQueries++;
            return Task.FromResult(new CameraControlsSnapshot(IsRunning, Epoch, DateTimeOffset.UtcNow, "queried", new Dictionary<string, CameraControlInfo>
            {
                ["brightness"] = new("Brightness", "driver_units", "available", true, 0, 100, 1, Brightness, false, false),
                ["contrast"] = new("Contrast", "driver_units", "available", true, 0, 200, 1, Contrast, false, false)
            }));
        }

        public Task<CameraImageControlResult> SetImageControlsAsync(string device, long epoch,
            double? brightness = null, double? contrast = null, CancellationToken cancellation = default)
        {
            var request = new WriteRequest(device, epoch, brightness, contrast, cancellation);
            Calls.Enqueue(request);
            return Write?.Invoke(request) ?? Task.FromResult(Complete(request));
        }

        public CameraImageControlResult Complete(WriteRequest request)
        {
            var before = new CameraImageValues(Brightness, Contrast);
            if (request.Cancellation.IsCancellationRequested) return new("cancelled", Epoch, false, before, before);
            bool changed = request.Brightness is { } b && b != Brightness || request.Contrast is { } c && c != Contrast;
            Brightness = request.Brightness ?? Brightness;
            Contrast = request.Contrast ?? Contrast;
            return new(changed ? "applied" : "unchanged", Epoch, changed, before, new(Brightness, Contrast));
        }
    }
}

namespace ProjectTabletop.App
{
    public sealed partial class MainWindow : IDisposable
    {
        private enum AnnotationMode { None, BoardCalibration, TopCalibration }
        private readonly string _appDataDirectory = Path.Combine(Path.GetTempPath(), "tabletop-picture-lifecycle-" + Guid.NewGuid());
        private readonly CameraCaptureService _camera = new();
        private readonly SemaphoreSlim _cameraOperation = new(1, 1);
        private readonly TestScene _scene = new();
        private readonly TestInputWorker _cameraInputWorker = new();
        private readonly TestDispatcher DispatcherQueue = new();
        private readonly TestControl CameraPictureProfilesToggle = new(), CaptureCameraPictureDefaultButton = new(), CameraPictureStatusText = new();
        private bool _closing = false, _boardSetupActive = false, _cameraManualRestartRequired = false, _cameraWanted = true;
        private string? _cameraWantedDeviceId = "test-camera";
        private AnnotationMode _annotationMode = AnnotationMode.None;
        private long _cameraInputNotBeforeTicks;

        public CameraCaptureService Camera => _camera;
        public SemaphoreSlim OperationGate => _cameraOperation;
        public int HandResetsForTest { get; private set; }
        public int MarkerResetsForTest { get; private set; }
        public int InputClearsForTest => _cameraInputWorker.Clears;
        public long InputNotBeforeForTest => _cameraInputNotBeforeTicks;
        public string SettingsPathForTest => CameraPictureSettingsPath;
        public string ActiveForTest => _cameraPictureActive;
        public bool RestorePendingForTest => _cameraPictureRestoreNeeded;
        public double? CachedContrastForTest => _cameraPictureSession?.Controls.GetValueOrDefault("contrast")?.Value;

        public async Task InitializeForTest()
        {
            InitializeCameraPictureProfiles();
            await InitializeSessionForTest();
        }
        public async Task InitializeSessionForTest()
        {
            await _cameraOperation.WaitAsync();
            try { await InitializeCameraPictureSessionAsync(); }
            finally { _cameraOperation.Release(); }
        }
        public void OpenForTest(BoardScreen screen, bool dispatch = true)
        {
            _scene.CurrentBoardScreen = screen;
            BoardOpenedForCameraPicture(screen);
            if (dispatch) DispatcherQueue.Drain();
        }
        public void RequestForTest() => RequestCameraPictureProfile();
        public Task PrepareCalibrationForTest() => PrepareCameraPictureCalibrationAsync();
        public void FinishCalibrationForTest() => FinishCameraPictureCalibration();
        public async Task CloseAndRestoreForTest()
        {
            _closing = true;
            CancelCameraPictureRequest();
            await _cameraOperation.WaitAsync();
            try { await RestoreCameraPictureDefaultAsync(); }
            finally { _cameraOperation.Release(); }
        }
        public async Task WaitForIdleForTest()
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (true)
            {
                DispatcherQueue.Drain();
                if (!Volatile.Read(ref _cameraPictureWorkerRunning) && !Volatile.Read(ref _cameraPicturePending)) return;
                await Task.Delay(1, timeout.Token);
            }
        }
        private void ClearHandTracking(string reason, bool preserveBoardAnimations, bool preserveRecording)
        {
            Assert.Equal("camera-picture", reason);
            Assert.True(preserveBoardAnimations);
            Assert.True(preserveRecording);
            HandResetsForTest++;
        }
        private void ResetMarkerTracking(string reason)
        {
            Assert.Equal("camera-picture", reason);
            MarkerResetsForTest++;
        }
        public void Dispose()
        {
            _closing = true;
            CancelCameraPictureRequest();
            if (Directory.Exists(_appDataDirectory)) Directory.Delete(_appDataDirectory, true);
        }
        private sealed class TestScene
        {
            public bool HasBoardMediaClip = true;
            public BoardScreen CurrentBoardScreen = BoardScreen.Menu;
        }
        private sealed class TestInputWorker { public int Clears; public void Clear() => Clears++; }
        private sealed class TestControl { public bool IsOn, IsEnabled; public string Text = ""; }
        private sealed class TestDispatcher
        {
            private readonly ConcurrentQueue<Action> _queue = new();
            public bool TryEnqueue(Action action) { _queue.Enqueue(action); return true; }
            public void Drain() { while (_queue.TryDequeue(out var action)) action(); }
        }
    }
}
