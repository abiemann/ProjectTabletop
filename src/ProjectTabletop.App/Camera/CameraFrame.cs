namespace ProjectTabletop.App.Camera;

/// <summary>
/// An owned, tightly packed BGRA8 camera frame. The pixel array must be treated as read-only.
/// Camera frames are separate from the 4K projector render path.
/// Timestamp is host UTC after the CPU copy, shared by inference diagnostics and video
/// recording. It is not a measured hardware exposure timestamp.
/// </summary>
public sealed record CameraFrame(
    int Width,
    int Height,
    int Stride,
    byte[] Bgra,
    DateTimeOffset Timestamp);
