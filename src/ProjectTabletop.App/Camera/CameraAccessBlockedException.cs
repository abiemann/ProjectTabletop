namespace ProjectTabletop.App.Camera;

/// <summary>Opening the camera requires a deliberate user retry, not background probing.</summary>
internal sealed class CameraAccessBlockedException(string message, Exception? inner = null)
    : InvalidOperationException(message, inner)
{
    internal static bool IsContention(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
            if (current is CameraAccessBlockedException ||
                current.HResult is unchecked((int)0x80070020) or unchecked((int)0x800700AA))
                return true; // ERROR_SHARING_VIOLATION / ERROR_BUSY
        return false;
    }
}
