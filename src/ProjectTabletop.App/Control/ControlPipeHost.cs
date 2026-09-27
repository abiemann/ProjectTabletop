using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace ProjectTabletop.App.Control;

/// <summary>Local, current-user control channel. One bounded JSON command per connection.</summary>
internal sealed class ControlPipeHost : IAsyncDisposable
{
    internal const string PipeName = "ProjectTabletop.Control.v1";
    private const int MaxLineCharacters = 16_384;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly Func<string, CancellationToken, Task<object?>> _handle;
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _loop;

    internal ControlPipeHost(Func<string, CancellationToken, Task<object?>> handle)
    {
        _handle = handle;
        _loop = Task.Run(RunAsync);
    }

    private async Task RunAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            try
            {
                await using var pipe = new NamedPipeServerStream(PipeName, PipeDirection.InOut, 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync(_stop.Token);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
                timeout.CancelAfter(TimeSpan.FromSeconds(60));
                await ServeAsync(pipe, timeout.Token);
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                AppLog.Write("Control pipe", ex);
                try { await Task.Delay(250, _stop.Token); }
                catch (OperationCanceledException) { break; }
            }
        }
    }

    private async Task ServeAsync(Stream pipe, CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(pipe, new UTF8Encoding(false), false, 1024, leaveOpen: true);
        using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 1024, leaveOpen: true)
            { AutoFlush = true };
        object response;
        try
        {
            var line = await ReadLineBoundedAsync(reader, cancellationToken);
            using var document = JsonDocument.Parse(line);
            if (!document.RootElement.TryGetProperty("method", out var methodElement) ||
                methodElement.ValueKind != JsonValueKind.String ||
                string.IsNullOrWhiteSpace(methodElement.GetString()))
                throw new InvalidDataException("A method string is required.");
            response = new { ok = true, result = await _handle(methodElement.GetString()!, cancellationToken) };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            response = new { ok = false, error = ex.Message };
        }
        var json = JsonSerializer.Serialize(response, JsonOptions);
        if (json.Length > MaxLineCharacters)
            json = JsonSerializer.Serialize(new { ok = false, error = "Response exceeds size limit." });
        await writer.WriteLineAsync(json.AsMemory(), cancellationToken);
    }

    private static async Task<string> ReadLineBoundedAsync(StreamReader reader, CancellationToken token)
    {
        var buffer = new char[1];
        var line = new StringBuilder();
        while (true)
        {
            if (await reader.ReadAsync(buffer.AsMemory(), token) == 0)
                throw new EndOfStreamException("Control client disconnected.");
            if (buffer[0] == '\n') return line.ToString();
            if (buffer[0] != '\r') line.Append(buffer[0]);
            if (line.Length > MaxLineCharacters)
                throw new InvalidDataException("Command exceeds size limit.");
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        await _loop;
        _stop.Dispose();
    }
}
