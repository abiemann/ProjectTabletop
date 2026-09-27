using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

if (args.Length == 0)
{
    try
    {
        await RunMcpAsync();
        return 0;
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine(ex);
        return 1;
    }
}

if (args.Length < 2 || args[0] != "--once" || args.Length > 3)
{
    Console.Error.WriteLine("Usage: ProjectTabletop.ControlMcp --once <get_status|start_board_scan|rescan_board|black_output|capture_raw_frame|capture_projection_preview|stop_scan|start_camera|stop_camera|open_output|set_background_media|set_hand_tracking|show_test_grid|show_board_menu|show_hand_tracking_test|show_photo_copy|shutdown> [JSON object]");
    return 2;
}

static async Task RunMcpAsync()
{
    static McpServerTool Tool(string name, string description) =>
        McpServerTool.Create((Func<Task<string>>)(() => CallToolAsync(name)),
            new McpServerToolCreateOptions { Name = name, Description = description });
    static McpServerTool BackgroundMediaTool() =>
        McpServerTool.Create((Func<string, Task<string>>)SetBackgroundMediaAsync,
            new McpServerToolCreateOptions
            {
                Name = "set_background_media",
                Description = "Load an image or video from an absolute local path as the board-clipped background."
            });
    static McpServerTool HandTrackingTool() =>
        McpServerTool.Create((Func<bool, Task<string>>)SetHandTrackingAsync,
            new McpServerToolCreateOptions
            {
                Name = "set_hand_tracking",
                Description = "Enable or disable hand tracking, camera fingertip markers, and white hand spotlights on the registered board."
            });
    var options = new McpServerOptions
    {
        ServerInfo = new Implementation { Name = "ProjectTabletop.ControlMcp", Version = "0.1.0" },
        ToolCollection =
        [
            Tool("get_status", "Read camera, output, cardboard scan, and fingertip tracking status from the local ProjectTabletop app."),
            Tool("start_board_scan", "Show the white projector scan and begin detecting the physical cardboard."),
            Tool("rescan_board", "Restart white-flood cardboard detection and projector registration while board setup is active."),
            Tool("black_output", "Show full black on the existing projector output while keeping the webcam running."),
            Tool("capture_raw_frame", "Save one raw webcam PNG and return its absolute local path."),
            Tool("stop_scan", "Stop the cardboard scan and remove the scan projection."),
            Tool("start_camera", "Start or reconnect the selected webcam."),
            Tool("stop_camera", "Stop the webcam."),
            Tool("open_output", "Open the projector output on the selected display."),
            BackgroundMediaTool(),
            HandTrackingTool(),
            Tool("show_test_grid", "Show the board-clipped test grid on the projector."),
            Tool("show_board_menu", "Show the board app menu and enable hand tracking for pinch selection."),
            Tool("show_hand_tracking_test", "Open the built-in Hand-Tracking gesture test and enable hand tracking."),
            Tool("show_photo_copy", "Open Photo Copy on white output; a pinch photographs a separate object or the other hand."),
            Tool("capture_projection_preview", "Save the current compositor scene as a PNG, without capturing desktop windows."),
            Tool("shutdown", "Close the local ProjectTabletop app cleanly.")
        ]
    };
    await using var server = McpServer.Create(new StdioServerTransport("ProjectTabletop.ControlMcp"), options);
    await server.RunAsync();
}

static Task<string> SetBackgroundMediaAsync(string path) =>
    CallToolAsync("set_background_media", JsonSerializer.SerializeToElement(new { path }));

static Task<string> SetHandTrackingAsync(bool enabled) =>
    CallToolAsync("set_hand_tracking", JsonSerializer.SerializeToElement(new { enabled }));

static async Task<string> CallToolAsync(string method, JsonElement? parameters = null)
{
    try
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(65));
        using var empty = JsonDocument.Parse("{}");
        var response = await ControlPipeClient.CallAsync(method, parameters ?? empty.RootElement, timeout.Token);
        return response.GetRawText();
    }
    catch (Exception ex)
    {
        return JsonSerializer.Serialize(new { ok = false, error = ex.Message });
    }
}

try
{
    JsonElement parameters = args.Length == 3
        ? JsonDocument.Parse(args[2]).RootElement.Clone()
        : JsonDocument.Parse("{}").RootElement.Clone();
    if (parameters.ValueKind != JsonValueKind.Object)
        throw new ArgumentException("Parameters must be a JSON object.");
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(65));
    var response = await ControlPipeClient.CallAsync(args[1], parameters, timeout.Token);
    Console.WriteLine(response.GetRawText());
    return response.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.True ? 0 : 1;
}
catch (Exception ex)
{
    Console.Error.WriteLine(ex.Message);
    return 1;
}

internal static class ControlPipeClient
{
    private const string PipeName = "ProjectTabletop.Control.v1";

    internal static async Task<JsonElement> CallAsync(string method, JsonElement parameters,
        CancellationToken cancellationToken = default)
    {
        using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await pipe.ConnectAsync(5_000, cancellationToken);
        using var reader = new StreamReader(pipe, new UTF8Encoding(false), false, 1024, leaveOpen: true);
        using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 1024, leaveOpen: true)
            { AutoFlush = true };
        var request = JsonSerializer.Serialize(new { method, @params = parameters });
        if (request.Length > 16_384) throw new InvalidOperationException("Command exceeds size limit.");
        await writer.WriteLineAsync(request.AsMemory(), cancellationToken);
        var line = await ReadBoundedLineAsync(reader, cancellationToken);
        using var response = JsonDocument.Parse(line);
        return response.RootElement.Clone();
    }

    private static async Task<string> ReadBoundedLineAsync(StreamReader reader, CancellationToken token)
    {
        var buffer = new char[1];
        var line = new StringBuilder();
        while (true)
        {
            if (await reader.ReadAsync(buffer.AsMemory(), token) == 0)
                throw new EndOfStreamException("The app closed the control pipe without a response.");
            if (buffer[0] == '\n') return line.ToString();
            if (buffer[0] != '\r') line.Append(buffer[0]);
            if (line.Length > 16_384) throw new InvalidDataException("Response exceeds size limit.");
        }
    }
}
