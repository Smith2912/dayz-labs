using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using Dzl.Core.App;

namespace Dzl.Core.Ipc;

// Hosts the dispatcher on a named pipe. One request line -> one response line.
// Run on a background task from the tray; cancel via the token.
public sealed class PipeServer
{
    private readonly Func<LauncherService> _svc;
    private readonly string _pipeName;
    public PipeServer(Func<LauncherService> svc, string? pipeName = null) { _svc = svc; _pipeName = pipeName ?? IpcContract.PipeName; }

    public async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            using var pipe = new NamedPipeServerStream(_pipeName, PipeDirection.InOut, 1,
                PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            try { await pipe.WaitForConnectionAsync(ct); }
            catch (OperationCanceledException) { break; }
            try
            {
                using var reader = new StreamReader(pipe, Encoding.UTF8, false, 1024, leaveOpen: true);
                using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 1024, leaveOpen: true) { AutoFlush = true };
                using var requestTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                requestTimeout.CancelAfter(TimeSpan.FromSeconds(10));
                var line = await ReadLineBoundedAsync(reader, requestTimeout.Token);
                if (line is null) continue;
                IpcRequest? req;
                try { req = JsonSerializer.Deserialize<IpcRequest>(line, IpcContract.Json); }
                catch (JsonException)
                {
                    await writer.WriteLineAsync(JsonSerializer.Serialize(new IpcResponse(false, "bad request", null), IpcContract.Json));
                    continue;
                }
                var resp = req is null
                    ? new IpcResponse(false, "bad request", null)
                    : IpcDispatcher.Handle(req, _svc());
                await writer.WriteLineAsync(JsonSerializer.Serialize(resp, IpcContract.Json));
            }
            catch (InvalidDataException) { /* oversized request; close this connection only */ }
            catch (IOException) { /* client vanished; loop */ }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { /* stalled client; accept another */ }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
        }
    }

    private static async Task<string?> ReadLineBoundedAsync(StreamReader reader, CancellationToken ct)
    {
        const int limit = 1024 * 1024;
        var result = new StringBuilder();
        var buffer = new char[1024];
        while (true)
        {
            var count = await reader.ReadAsync(buffer.AsMemory(), ct);
            if (count == 0) return result.Length == 0 ? null : result.ToString();
            var end = Array.IndexOf(buffer, '\n', 0, count);
            var take = end >= 0 ? end : count;
            if (result.Length + take > limit) throw new InvalidDataException("Automation request is too large.");
            result.Append(buffer, 0, take);
            if (end >= 0) return result.ToString().TrimEnd('\r');
        }
    }
}
