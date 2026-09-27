#if DEBUG
using ProjectTabletop.Vision;
using Windows.Graphics.Imaging;
using Windows.Storage;

namespace ProjectTabletop.App;

public sealed partial class MainWindow
{
    // Saves only synthetic object fixtures in a private test directory. The
    // user's capture, Pictures folder, camera and projected board are untouched.
    private async Task<object> VerifyPhotoCopySaveAsync()
    {
        var liveOutput = _output;
        var liveState = (_camera.IsRunning, _output?.AppWindow.IsVisible,
            Volatile.Read(ref _boardSetupActive), _scene.CurrentBoardScreen,
            _scene.HasBoardMediaClip, _scene.BlackjackState.Revision);
        string directory = Path.Combine(_appDataDirectory, "PhotoCopySaveVerification", Guid.NewGuid().ToString("N"));
        const int width = 37, height = 23;
        var pixels = Fixture(width, height);
        var original = pixels.ToArray();
        var cutout = new PhotoHandCutout(width, height, pixels, new(17, 13), new(0, -1))
        {
            CameraGeometry = new(1920, 1080, [1.0 / 1920, 0, 0, 0, 1.0 / 1080, 0, 0, 0, 1])
        };
        string first = await PhotoCopyImageStore.SaveAsync(cutout, directory);
        await AssertImage(first, width, height, original);
        Require(pixels.SequenceEqual(original), "Saving modified the original object pixels.");
        byte[] firstFile = await File.ReadAllBytesAsync(first);

        string second = await PhotoCopyImageStore.SaveAsync(cutout, directory);
        byte[] firstFileAfterSecondSave = await File.ReadAllBytesAsync(first);
        Require(!string.Equals(first, second, StringComparison.OrdinalIgnoreCase) &&
                firstFile.SequenceEqual(firstFileAfterSecondSave),
            "Saving a second image reused a filename or overwrote the earlier photo.");

        // Deliberately non-square, padded rows; padding bytes must not become pixels.
        const int paddedWidth = 19, paddedHeight = 11, stride = paddedWidth * 4 + 13;
        var packed = Fixture(paddedWidth, paddedHeight);
        var padded = Enumerable.Repeat((byte)213, stride * paddedHeight).ToArray();
        for (int row = 0; row < paddedHeight; row++)
            Buffer.BlockCopy(packed, row * paddedWidth * 4, padded, row * stride, paddedWidth * 4);
        string paddedPath = await PhotoCopyImageStore.SaveBgraAsync(paddedWidth, paddedHeight,
            stride, padded, directory);
        await AssertImage(paddedPath, paddedWidth, paddedHeight, packed);

        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        string cancelledDirectory = Path.Combine(directory, "cancelled");
        await MustFail<OperationCanceledException>(() => PhotoCopyImageStore.SaveAsync(cutout,
            cancelledDirectory, cancelled.Token), "A cancelled capture reported a saved image.");
        Require(!Directory.Exists(cancelledDirectory), "An already-cancelled save created output files.");

        string invalidDirectory = Path.Combine(directory, "invalid");
        await MustFail<ArgumentException>(() => PhotoCopyImageStore.SaveAsync(
            cutout with { BgraPixels = new byte[10] }, invalidDirectory),
            "An invalid object pixel buffer reported a saved image.");
        await MustFail<ArgumentException>(() => PhotoCopyImageStore.SaveBgraAsync(19, 11, 12,
            padded, invalidDirectory), "An invalid row stride reported a saved image.");
        Require(!Directory.Exists(invalidDirectory), "Invalid pixel input created an output directory.");

        // An existing file is a deterministic unwritable destination, independent
        // of administrator privileges or the machine's filesystem permissions.
        string blocked = Path.Combine(directory, "not-a-directory");
        byte[] marker = [11, 22, 33, 44];
        await File.WriteAllBytesAsync(blocked, marker);
        await MustFail<IOException>(() => PhotoCopyImageStore.SaveAsync(cutout, blocked),
            "A failed filesystem write reported a saved image.");
        byte[] markerAfterFailure = await File.ReadAllBytesAsync(blocked);
        Require(marker.SequenceEqual(markerAfterFailure),
            "The failed save changed an existing file.");
        Require(Directory.GetFiles(directory, "*.png").Length == 3 &&
                Directory.GetFiles(directory, "*.tmp").Length == 0,
            "A failed save published a PNG or left a partial temporary image.");

        Require(ReferenceEquals(liveOutput, _output) && liveState == (_camera.IsRunning, _output?.AppWindow.IsVisible,
                Volatile.Read(ref _boardSetupActive), _scene.CurrentBoardScreen,
                _scene.HasBoardMediaClip, _scene.BlackjackState.Revision),
            "The isolated image-save check changed live hardware or gameplay.");
        return new { passed = true, exactNativePixels = true, straightAlphaPreserved = true,
            transparentBorderAndHole = true, paddedRows = true, uniqueFiles = true,
            cancellationAndFailuresCannotReportSuccess = true, liveHardwareUnchanged = true,
            directory, images = new[] { first, second, paddedPath } };

        static byte[] Fixture(int imageWidth, int imageHeight)
        {
            var result = new byte[imageWidth * imageHeight * 4];
            for (int y = 0; y < imageHeight; y++)
            for (int x = 0; x < imageWidth; x++)
            {
                int offset = (y * imageWidth + x) * 4;
                result[offset] = (byte)(23 + x * 3);
                result[offset + 1] = (byte)(41 + y * 5);
                result[offset + 2] = (byte)(190 - x * 2);
                bool exterior = x < 2 || y < 2 || x >= imageWidth - 2 || y >= imageHeight - 2;
                bool hole = x >= imageWidth / 2 - 1 && x <= imageWidth / 2 + 1 &&
                    y >= imageHeight / 2 - 1 && y <= imageHeight / 2 + 1;
                result[offset + 3] = exterior || hole ? (byte)0 :
                    x == 2 ? (byte)1 : x == 3 ? (byte)64 : y == 2 ? (byte)128 :
                    y == 3 ? (byte)254 : (byte)255;
            }
            return result;
        }

        static async Task AssertImage(string path, int expectedWidth, int expectedHeight, byte[] expected)
        {
            var file = await StorageFile.GetFileFromPathAsync(path);
            using var stream = await file.OpenReadAsync();
            var decoder = await BitmapDecoder.CreateAsync(stream);
            Require(decoder.DecoderInformation.CodecId == BitmapDecoder.PngDecoderId &&
                    decoder.PixelWidth == expectedWidth && decoder.PixelHeight == expectedHeight,
                "Export was not a PNG at the exact original pixel dimensions.");
            var data = await decoder.GetPixelDataAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Straight,
                new BitmapTransform(), ExifOrientationMode.IgnoreExifOrientation, ColorManagementMode.DoNotColorManage);
            byte[] actual = data.DetachPixelData();
            Require(actual.SequenceEqual(expected),
                "PNG changed source colors, channel order, partial alpha, or transparent pixels.");
            Require(actual[3] == 0 && actual[((expectedHeight / 2) * expectedWidth + expectedWidth / 2) * 4 + 3] == 0,
                "PNG lost its transparent exterior or enclosed hole.");
        }

        static async Task MustFail<T>(Func<Task<string>> action, string message) where T : Exception
        {
            try { await action(); }
            catch (T) { return; }
            throw new InvalidOperationException(message);
        }
        static void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }
    }
}
#endif
