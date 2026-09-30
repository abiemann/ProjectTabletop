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
    Console.Error.WriteLine("Usage: ProjectTabletop.ControlMcp --once <get_status|start_board_scan|rescan_board|black_output|capture_raw_frame|capture_projection_preview|stop_scan|start_camera|stop_camera|open_output|set_background_media|set_hand_tracking|show_test_grid|show_board_menu|show_hand_tracking_test|show_photo_copy|show_blackjack|show_monopoly|show_globe|show_slots|show_settings|show_paint|blackjack_action|monopoly_action|globe_action|slots_action|slots_demo|capture_blackjack_preview|capture_monopoly_preview|capture_globe_preview|capture_slots_preview|shutdown> [JSON object]");
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
    static McpServerTool BlackjackActionTool() =>
        McpServerTool.Create((Func<string, Task<string>>)BlackjackActionAsync,
            new McpServerToolCreateOptions
            {
                Name = "blackjack_action",
                Description = "Activate an enabled Blackjack button by id, such as bj-deal, bj-hit, bj-stand, bj-double, bj-split, bj-bet-25 or bj-reset. Uses virtual credits only."
            });
    static McpServerTool MonopolyActionTool() =>
        McpServerTool.Create((Func<string, Task<string>>)MonopolyActionAsync,
            new McpServerToolCreateOptions
            {
                Name = "monopoly_action",
                Description = "Activate an enabled Monopoly button by its current id. Player setup, turn actions and save-on-exit use the same board controls."
            });
    static McpServerTool GlobeActionTool() =>
        McpServerTool.Create((Func<string, Task<string>>)GlobeActionAsync,
            new McpServerToolCreateOptions
            {
                Name = "globe_action",
                Description = "Open Globe controls with globe-drawer-open, close with globe-drawer-close, or activate visible globe-zoom-in, globe-zoom-out or globe-exit controls."
            });
    static McpServerTool SlotsActionTool() =>
        McpServerTool.Create((Func<string, Task<string>>)SlotsActionAsync,
            new McpServerToolCreateOptions
            {
                Name = "slots_action",
                Description = "Activate an enabled Dragon Slots control by id: slot-spin, slot-bet-up, slot-bet-down, slot-buy, slot-refill or slot-exit. Uses virtual credits only."
            });
    static McpServerTool SlotsDemoTool() =>
        McpServerTool.Create((Func<string, Task<string>>)SlotsDemoAsync,
            new McpServerToolCreateOptions
            {
                Name = "slots_demo",
                Description = "Make the next Dragon Slots spin land a feature: Respins, FreeSpins or Vault."
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
            Tool("show_photo_copy", "Open Photo Copy on grey output: bottom controls offer Exit, Swirl and Copy. Swirl repeats an object photo and becomes Clear; Copy immediately saves a PNG with a photocopier sound."),
            Tool("show_paint", "Open GPU fluid Paint: physical disturbances deposit slowly settling coats of paint and metallic particles. Exit and Save float at the bottom sides; only interference with their text enables a local button light. Canvas hand spotlights stay disabled."),
            Tool("show_blackjack", "Open the virtual-chip Blackjack table and laptop preview without opening projector output or starting the camera."),
            BlackjackActionTool(),
            Tool("show_monopoly", "Open the regal Monopoly board with human/AI player setup and a clickable laptop preview, without starting camera or projector."),
            MonopolyActionTool(),
            Tool("show_globe", "Open the high-resolution Earth globe with a distant arrival, slow spin and clickable zoom/rotate controls, without starting camera or projector."),
            GlobeActionTool(),
            Tool("show_slots", "Open Dragon Slots: a five-reel, 40-line dragon slot machine with long-press controls, Dragonfire Respins, free spins and the Treasure Vault, with a clickable laptop preview."),
            SlotsActionTool(),
            SlotsDemoTool(),
            Tool("capture_slots_preview", "Save the current Dragon Slots machine as an unmapped laptop-preview PNG."),
            Tool("show_settings", "Open the Settings board, which holds the Hand-Tracking tester."),
            Tool("capture_globe_preview", "Save the current Globe view as a 3840 × 2160 unmapped laptop-preview PNG."),
            Tool("capture_monopoly_preview", "Save the current Monopoly board as an unmapped laptop-preview PNG."),
            Tool("capture_blackjack_preview", "Save the current Blackjack table as an unmapped laptop-preview PNG."),
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

static Task<string> BlackjackActionAsync(string id) =>
    CallToolAsync("blackjack_action", JsonSerializer.SerializeToElement(new { id }));

static Task<string> MonopolyActionAsync(string id) =>
    CallToolAsync("monopoly_action", JsonSerializer.SerializeToElement(new { id }));

static Task<string> SlotsActionAsync(string id) =>
    CallToolAsync("slots_action", JsonSerializer.SerializeToElement(new { id }));

static Task<string> SlotsDemoAsync(string feature) =>
    CallToolAsync("slots_demo", JsonSerializer.SerializeToElement(new { feature }));

static Task<string> GlobeActionAsync(string id) =>
    CallToolAsync("globe_action", JsonSerializer.SerializeToElement(new { id }));

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
