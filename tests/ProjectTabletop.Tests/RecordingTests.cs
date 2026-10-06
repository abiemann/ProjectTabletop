using System.Text.Json;
using ProjectTabletop.App;

namespace ProjectTabletop.Tests;

public class RecordingTests
{
    [Fact]
    public async Task BuiltInMjpegRecordsAndDecodesCameraTimeline()
    {
        string directory = Path.Combine(Path.GetTempPath(), "ProjectTabletop-recording-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var report = await HandTrackingVideoRecorderVerification.RunAsync(directory);
            var result = JsonSerializer.SerializeToElement(report);
            Assert.True(result.GetProperty("passed").GetBoolean());
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
}
