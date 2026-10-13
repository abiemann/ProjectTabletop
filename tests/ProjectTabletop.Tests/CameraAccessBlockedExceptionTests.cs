using System.Runtime.InteropServices;
using ProjectTabletop.App.Camera;

namespace ProjectTabletop.Tests;

public class CameraAccessBlockedExceptionTests
{
    [Theory]
    [InlineData(unchecked((int)0x80070020))]
    [InlineData(unchecked((int)0x800700AA))]
    public void NativeSharingAndBusyErrorsAreRecognizedThroughWrappers(int error)
    {
        var native = new COMException("Camera initialization failed.", error);
        Assert.True(CameraAccessBlockedException.IsContention(native));
        Assert.True(CameraAccessBlockedException.IsContention(new InvalidOperationException(
            "Capture initialization wrapper", new Exception("Async operation wrapper", native))));
    }

    [Fact]
    public void ExplicitBlockedErrorDoesNotRequireANativeBusyCode()
    {
        var blocked = new CameraAccessBlockedException("Another process holds this camera.");
        Assert.True(CameraAccessBlockedException.IsContention(blocked));
        Assert.True(CameraAccessBlockedException.IsContention(new Exception("Outer operation", blocked)));
    }

    [Fact]
    public void UnsupportedFormatsAndUnrelatedErrorsDoNotDisableFallback()
    {
        foreach (var error in new Exception[]
        {
            new NotSupportedException("Unsupported camera format."),
            new ArgumentException("Unsupported capture dimensions."),
            new COMException("Unrelated capture failure.", unchecked((int)0x80004005)),
            new InvalidOperationException("The word busy alone does not identify a sharing violation.")
        })
        {
            Assert.False(CameraAccessBlockedException.IsContention(error));
            Assert.False(CameraAccessBlockedException.IsContention(new Exception("Outer operation", error)));
        }
    }
}
