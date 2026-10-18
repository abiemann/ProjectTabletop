using ProjectTabletop.App;
using ProjectTabletop.App.Camera;
using ProjectTabletop.App.Settings;
using ProjectTabletop.Interaction;

namespace ProjectTabletop.Tests;

// Compiles the real profile coordinator partial against camera/UI test doubles.
// These checks do not exercise native driver setters or launch a window/camera.
public sealed class CameraPictureLifecycleTests
{
    [Fact]
    public async Task CancellationBeforeDriverAdmissionPreservesTheCacheForTheLatestGlobeRequest()
    {
        using var window = new MainWindow();
        await window.InitializeForTest();
        window.Camera.Calls.Clear();
        var entered = new TaskCompletionSource<CameraCaptureService.WriteRequest>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int requests = 0;
        window.Camera.Write = async request =>
        {
            if (Interlocked.Increment(ref requests) != 1) return window.Camera.Complete(request);
            entered.SetResult(request);
            await release.Task;
            // The lifecycle gate was never acquired: no reads and no setters happened.
            return new("cancelled", window.Camera.Epoch, false, new(null, null), new(null, null));
        };
        window.OpenForTest(BoardScreen.Globe);
        var first = await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        window.OpenForTest(BoardScreen.Globe);
        Assert.True(first.Cancellation.IsCancellationRequested);
        release.SetResult();
        await window.WaitForIdleForTest();

        Assert.Equal(2, requests);
        Assert.All(window.Camera.Calls, call => Assert.Equal(100, call.Contrast));
        Assert.Equal(100, window.Camera.Contrast);
        Assert.Equal(100, window.CachedContrastForTest);
        Assert.Equal("Dark scene", window.ActiveForTest);
        Assert.True(window.RestorePendingForTest);
    }

    [Fact]
    public async Task UnknownValuesAfterASetterStayUnknownUntilRestorationReadsTheDriverAgain()
    {
        using var window = new MainWindow();
        await window.InitializeForTest();
        int requests = 0;
        window.Camera.Write = request =>
        {
            if (Interlocked.Increment(ref requests) != 1) return Task.FromResult(window.Camera.Complete(request));
            window.Camera.Contrast = 100;
            return Task.FromResult(new CameraCaptureService.CameraImageControlResult(
                "failed", window.Camera.Epoch, true, new(50, 116), new(null, null),
                Error: "A setter was attempted but readback was unavailable."));
        };
        window.OpenForTest(BoardScreen.Globe);
        await window.WaitForIdleForTest();
        Assert.Null(window.CachedContrastForTest);
        Assert.True(window.RestorePendingForTest);
        int queriesBeforeRestoration = window.Camera.ControlQueries;

        window.OpenForTest(BoardScreen.Menu);
        await window.WaitForIdleForTest();
        Assert.True(window.Camera.ControlQueries > queriesBeforeRestoration);
        Assert.Equal(116, window.Camera.Contrast);
        Assert.Equal(116, window.CachedContrastForTest);
        Assert.Equal("Default", window.ActiveForTest);
        Assert.False(window.RestorePendingForTest);
    }

    [Fact]
    public async Task TwoBoardChangesInOneUiTurnNeverApplyTheIntermediateDarkProfile()
    {
        using var window = new MainWindow();
        await window.InitializeForTest();
        window.Camera.Calls.Clear();
        window.OpenForTest(BoardScreen.Globe, dispatch: false);
        window.OpenForTest(BoardScreen.Menu, dispatch: false);
        await window.WaitForIdleForTest();
        Assert.DoesNotContain(window.Camera.Calls, call => call.Contrast == 100);
        Assert.Equal(116, window.Camera.Contrast);
        Assert.Equal(0, window.HandResetsForTest);
    }

    [Fact]
    public async Task NavigationWhileQueuedAppliesOnlyTheLatestBoardAndDoesNotResetForANoOp()
    {
        using var window = new MainWindow();
        await window.InitializeForTest();
        window.Camera.Calls.Clear();
        await window.OperationGate.WaitAsync(TestContext.Current.CancellationToken);
        window.OpenForTest(BoardScreen.Globe);
        window.OpenForTest(BoardScreen.Menu);
        Assert.Empty(window.Camera.Calls);
        window.OperationGate.Release();
        await window.WaitForIdleForTest();

        Assert.DoesNotContain(window.Camera.Calls, call => call.Contrast == 100);
        Assert.Equal(116, window.Camera.Contrast);
        Assert.Equal("Default", window.ActiveForTest);
        Assert.False(window.RestorePendingForTest);
        Assert.Equal(0, window.HandResetsForTest);
        Assert.Equal(0, window.MarkerResetsForTest);
    }

    [Fact]
    public async Task LeavingGlobeCancelsAnInflightDarkRequestThenConfirmsDefault()
    {
        using var window = new MainWindow();
        await window.InitializeForTest();
        window.Camera.Calls.Clear();
        var entered = new TaskCompletionSource<CameraCaptureService.WriteRequest>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        window.Camera.Write = async request =>
        {
            if (request.Contrast != 100) return window.Camera.Complete(request);
            Assert.True(window.HandResetsForTest > 0);
            Assert.True(window.MarkerResetsForTest > 0);
            entered.TrySetResult(request);
            await release.Task;
            return new("cancelled", window.Camera.Epoch, true, new(50, 116), new(50, 116), true, true);
        };
        window.OpenForTest(BoardScreen.Globe);
        var dark = await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.Equal(0, window.OperationGate.CurrentCount);

        window.OpenForTest(BoardScreen.Menu);
        Assert.True(dark.Cancellation.IsCancellationRequested);
        release.SetResult();
        await window.WaitForIdleForTest();

        Assert.Equal(100, window.Camera.Calls.First().Contrast);
        Assert.All(window.Camera.Calls.Skip(1), call => Assert.Equal(116, call.Contrast));
        Assert.Equal(116, window.Camera.Contrast);
        Assert.Equal("Default", window.ActiveForTest);
        Assert.False(window.RestorePendingForTest);
        Assert.True(window.HandResetsForTest >= 2);
        Assert.Equal(window.HandResetsForTest, window.MarkerResetsForTest);
        Assert.Equal(window.HandResetsForTest, window.InputClearsForTest);
        Assert.True(window.InputNotBeforeForTest > 0);
        Assert.Equal(1, window.OperationGate.CurrentCount);
    }

    [Fact]
    public async Task CalibrationRestoresDefaultBeforeProceedingAndDarkReturnsOnlyAfterFinishing()
    {
        using var window = new MainWindow();
        await window.InitializeForTest();
        window.Camera.Calls.Clear();
        window.OpenForTest(BoardScreen.Globe);
        await window.WaitForIdleForTest();
        Assert.Equal(100, window.Camera.Contrast);

        await window.PrepareCalibrationForTest();
        Assert.Equal(116, window.Camera.Contrast);
        Assert.Equal("Default", window.ActiveForTest);
        Assert.False(window.RestorePendingForTest);
        window.RequestForTest();
        await window.WaitForIdleForTest();
        Assert.DoesNotContain(window.Camera.Calls.Skip(1), call => call.Contrast == 100);

        window.FinishCalibrationForTest();
        await window.WaitForIdleForTest();
        Assert.Equal(100, window.Camera.Contrast);
        Assert.True(window.RestorePendingForTest);
    }

    [Fact]
    public async Task FirstDefaultIsPersistedBeforeAnyWriteAndRestartRestoresTheSavedValue()
    {
        using var window = new MainWindow();
        window.Camera.Write = request =>
        {
            var saved = CameraPictureSettingsStore.Read(window.SettingsPathForTest);
            Assert.Equal(116, saved.Cameras[window.Camera.ActiveDeviceId!].Contrast!.Value);
            return Task.FromResult(window.Camera.Complete(request));
        };
        await window.InitializeForTest();
        window.OpenForTest(BoardScreen.Globe);
        await window.WaitForIdleForTest();
        Assert.Equal(100, window.Camera.Contrast);

        await window.InitializeSessionForTest();
        Assert.Equal(116, window.Camera.Contrast);
        Assert.Equal(116, CameraPictureSettingsStore.Read(window.SettingsPathForTest)
            .Cameras[window.Camera.ActiveDeviceId!].Contrast!.Value);
    }

    [Fact]
    public async Task UnreadableSettingsPreventAllAutomaticDriverWrites()
    {
        using var window = new MainWindow();
        Directory.CreateDirectory(window.SettingsPathForTest);
        await window.InitializeForTest();
        window.OpenForTest(BoardScreen.Globe);
        await window.WaitForIdleForTest();
        Assert.Empty(window.Camera.Calls);
        Assert.Equal(116, window.Camera.Contrast);
        Assert.Equal(0, window.HandResetsForTest);
    }

    [Fact]
    public async Task DisabledStartupPreservesManualValuesAndDoesNotRecoverAnOldDarkDefault()
    {
        using var window = new MainWindow();
        CameraPictureSettingsStore.Write(window.SettingsPathForTest, new(false, new()
        {
            [window.Camera.ActiveDeviceId!] = new(new(50, 0, 100, 1), new(116, 0, 200, 1))
        }));
        window.Camera.Contrast = 92;
        await window.InitializeForTest();
        window.OpenForTest(BoardScreen.Globe);
        await window.WaitForIdleForTest();
        Assert.Empty(window.Camera.Calls);
        Assert.Equal(92, window.Camera.Contrast);
    }

    [Fact]
    public async Task UnconfirmedRestorationBlocksCalibrationAndFurtherDarkRequests()
    {
        using var window = new MainWindow();
        await window.InitializeForTest();
        window.Camera.Calls.Clear();
        window.OpenForTest(BoardScreen.Globe);
        await window.WaitForIdleForTest();
        window.Camera.Write = request => Task.FromResult(new CameraCaptureService.CameraImageControlResult(
            "failed", window.Camera.Epoch, false, new(50, 100), new(50, 100), Error: "Driver refused Default."));

        await Assert.ThrowsAsync<InvalidOperationException>(window.PrepareCalibrationForTest);
        Assert.True(window.RestorePendingForTest);
        Assert.Equal(100, window.Camera.Contrast);
        int calls = window.Camera.Calls.Count;
        window.FinishCalibrationForTest();
        await window.WaitForIdleForTest();
        Assert.Equal(calls, window.Camera.Calls.Count);
    }

    [Fact]
    public async Task ClosingRestoresDefaultWithoutTouchingDisposedInputWorkers()
    {
        using var window = new MainWindow();
        await window.InitializeForTest();
        window.OpenForTest(BoardScreen.Globe);
        await window.WaitForIdleForTest();
        int resets = window.HandResetsForTest;
        await window.CloseAndRestoreForTest();
        Assert.Equal(116, window.Camera.Contrast);
        Assert.False(window.RestorePendingForTest);
        Assert.Equal(resets, window.HandResetsForTest);
        Assert.Equal(resets, window.MarkerResetsForTest);
    }
}
