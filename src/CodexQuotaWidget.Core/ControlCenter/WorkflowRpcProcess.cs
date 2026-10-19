using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace CodexQuotaWidget.Core.ControlCenter;

/// <summary>Bounded newline JSON transport. Owns only the child process it starts; never logs payloads.</summary>
public sealed class WorkflowRpcProcess : IAsyncDisposable
{
    public const int MaximumFrameBytes = 8 * 1024 * 1024;
    readonly ProcessStartInfo startInfo;
    readonly bool includeJsonRpc;
    readonly CancellationTokenSource lifetime = new();
    readonly SemaphoreSlim writeGate = new(1, 1);
    readonly ConcurrentDictionary<long, TaskCompletionSource<JsonElement>> pending = new();
    Process? process;
    Task? stdoutPump, stderrPump;
    long nextId;
    int started, disposed, disconnected;
    string disconnectCode = "rpc_disconnected";

    public WorkflowRpcProcess(ProcessStartInfo startInfo, bool includeJsonRpc = true)
    { this.startInfo = startInfo; this.includeJsonRpc = includeJsonRpc; }

    public bool IsRunning => process is { } child && !lifetime.IsCancellationRequested && !HasExited(child);
    public event Action<JsonElement>? Notification;
    public event Action? Disconnected;
    public Func<JsonElement, CancellationToken, Task<object?>>? ServerRequestHandler { get; set; }

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(disposed != 0, this);
        if (Interlocked.Exchange(ref started, 1) != 0) throw new WorkflowRpcException("rpc_already_started");
        try
        {
            startInfo.UseShellExecute = false; startInfo.CreateNoWindow = true;
            startInfo.RedirectStandardInput = true; startInfo.RedirectStandardOutput = true; startInfo.RedirectStandardError = true;
            startInfo.StandardInputEncoding = new UTF8Encoding(false);
            process = new Process { StartInfo = startInfo };
            if (!process.Start()) throw new WorkflowRpcException("rpc_start_failed");
            stdoutPump = PumpStdoutAsync(process.StandardOutput.BaseStream);
            stderrPump = DiscardStderrAsync(process.StandardError.BaseStream);
            return Task.CompletedTask;
        }
        catch (Exception error) when (error is not OperationCanceledException)
        { Fail("rpc_start_failed"); throw new WorkflowRpcException("rpc_start_failed"); }
    }

    public async Task<JsonElement> CallAsync(string method, object? parameters = null,
        CancellationToken cancellationToken = default, TimeSpan? timeout = null)
    {
        if (!IsRunning) throw new WorkflowRpcException("rpc_not_connected");
        if (string.IsNullOrWhiteSpace(method) || method.Length > 128 || pending.Count >= 64)
            throw new WorkflowRpcException("rpc_request_limit");
        var requestTimeout = timeout ?? TimeSpan.FromMinutes(1);
        if (requestTimeout <= TimeSpan.Zero || requestTimeout > TimeSpan.FromHours(1))
            throw new WorkflowRpcException("rpc_invalid_timeout");
        using var deadline = new CancellationTokenSource(requestTimeout);
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime.Token, deadline.Token);
        var id = Interlocked.Increment(ref nextId);
        var completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!pending.TryAdd(id, completion)) throw new WorkflowRpcException("rpc_request_conflict");
        try
        {
            // One deadline covers queued writes, the complete frame/flush, and the response.
            await WriteAsync(Frame(method, parameters, id), budget.Token).ConfigureAwait(false);
            return await completion.Task.WaitAsync(budget.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { throw new WorkflowRpcException(deadline.IsCancellationRequested ? "rpc_timeout" : Volatile.Read(ref disconnectCode)); }
        finally { pending.TryRemove(id, out _); }
    }

    public Task NotifyAsync(string method, object? parameters = null, CancellationToken cancellationToken = default)
        => WriteAsync(Frame(method, parameters, null), cancellationToken);

    Dictionary<string, object?> Frame(string method, object? parameters, long? id)
    {
        var value = new Dictionary<string, object?> { ["method"] = method };
        if (includeJsonRpc) value["jsonrpc"] = "2.0";
        if (id is { } number) value["id"] = number;
        if (parameters is not null) value["params"] = parameters;
        return value;
    }

    async Task WriteAsync(object value, CancellationToken cancellationToken)
    {
        byte[] bytes;
        try { bytes = JsonSerializer.SerializeToUtf8Bytes(value); }
        catch (Exception error) when (error is JsonException or NotSupportedException) { throw new WorkflowRpcException("rpc_invalid_request"); }
        if (bytes.Length >= MaximumFrameBytes) throw new WorkflowRpcException("rpc_frame_limit");
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime.Token);
        await writeGate.WaitAsync(linked.Token).ConfigureAwait(false);
        var writeStarted = false;
        try
        {
            if (!IsRunning || process is null) throw new WorkflowRpcException("rpc_not_connected");
            linked.Token.ThrowIfCancellationRequested();
            writeStarted = true;
            // Some OS-backed streams cannot interrupt an already-started write. Bound the wait
            // as well; a cancelled frame invalidates this child transport before another write.
            await process.StandardInput.BaseStream.WriteAsync(bytes.AsMemory(), linked.Token).AsTask().WaitAsync(linked.Token).ConfigureAwait(false);
            await process.StandardInput.BaseStream.WriteAsync(new byte[] { 10 }.AsMemory(), linked.Token).AsTask().WaitAsync(linked.Token).ConfigureAwait(false);
            await process.StandardInput.BaseStream.FlushAsync(linked.Token).WaitAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Cancellation can leave an incomplete newline frame. Never append or replay on it.
            if (writeStarted) Fail("rpc_incomplete_write");
            throw;
        }
        catch (IOException) { Fail("rpc_write_failed"); throw new WorkflowRpcException("rpc_write_failed"); }
        finally { writeGate.Release(); }
    }

    async Task PumpStdoutAsync(Stream stream)
    {
        var buffer = new byte[8192];
        using var frame = new MemoryStream();
        try
        {
            while (true)
            {
                var count = await stream.ReadAsync(buffer, lifetime.Token).ConfigureAwait(false);
                if (count == 0) break;
                for (var index = 0; index < count; index++)
                {
                    if (buffer[index] == 10)
                    {
                        if (frame.Length > 0)
                        {
                            using var document = JsonDocument.Parse(frame.GetBuffer().AsMemory(0, (int)frame.Length), new() { MaxDepth = 64 });
                            Dispatch(document.RootElement.Clone());
                        }
                        frame.SetLength(0);
                    }
                    else
                    {
                        if (frame.Length >= MaximumFrameBytes) throw new WorkflowRpcException("rpc_frame_limit");
                        frame.WriteByte(buffer[index]);
                    }
                }
            }
            Fail(frame.Length == 0 ? "rpc_disconnected" : "rpc_incomplete_frame");
        }
        catch (OperationCanceledException) { Fail("rpc_disconnected"); }
        catch (WorkflowRpcException error) { Fail(error.Code); }
        catch (Exception) { Fail("rpc_invalid_response"); }
    }

    void Dispatch(JsonElement message)
    {
        if (message.ValueKind != JsonValueKind.Object) throw new WorkflowRpcException("rpc_invalid_response");
        if (message.TryGetProperty("method", out var method) && method.ValueKind == JsonValueKind.String)
        {
            if (message.TryGetProperty("id", out _)) _ = ReplyToServerAsync(message);
            else
            {
                try { Notification?.Invoke(message); } catch { /* Consumer exceptions do not expose payloads or stop transport. */ }
            }
            return;
        }
        if (!message.TryGetProperty("id", out var idValue) || !TryId(idValue, out var id))
            throw new WorkflowRpcException("rpc_invalid_response");
        if (!pending.TryRemove(id, out var completion)) return; // A cancelled/expired request is never replayed.
        if (message.TryGetProperty("error", out _)) completion.TrySetException(new WorkflowRpcException("rpc_remote_error"));
        else if (message.TryGetProperty("result", out var result)) completion.TrySetResult(result.Clone());
        else completion.TrySetException(new WorkflowRpcException("rpc_invalid_response"));
    }

    async Task ReplyToServerAsync(JsonElement request)
    {
        try
        {
            var response = new Dictionary<string, object?> { ["id"] = request.GetProperty("id").Clone() };
            if (includeJsonRpc) response["jsonrpc"] = "2.0";
            if (ServerRequestHandler is null)
                response["error"] = new { code = -32601, message = "Client request disabled" };
            else
            {
                try { response["result"] = await ServerRequestHandler(request, lifetime.Token).ConfigureAwait(false); }
                catch { response["error"] = new { code = -32603, message = "Client request rejected" }; }
            }
            await WriteAsync(response, lifetime.Token).ConfigureAwait(false);
        }
        catch { Fail("rpc_server_reply_failed"); }
    }

    async Task DiscardStderrAsync(Stream stream)
    {
        var discard = new byte[4096];
        try { while (await stream.ReadAsync(discard, lifetime.Token).ConfigureAwait(false) != 0) { } }
        catch { /* stderr is deliberately never retained or emitted. */ }
    }

    static bool TryId(JsonElement value, out long id)
    {
        if (value.ValueKind == JsonValueKind.Number) return value.TryGetInt64(out id);
        if (value.ValueKind == JsonValueKind.String) return long.TryParse(value.GetString(), out id);
        id = 0; return false;
    }
    static bool HasExited(Process child) { try { return child.HasExited; } catch { return true; } }
    void Fail(string code)
    {
        if (Interlocked.Exchange(ref disconnected, 1) != 0) return;
        Volatile.Write(ref disconnectCode, code);
        lifetime.Cancel();
        foreach (var entry in pending) if (pending.TryRemove(entry.Key, out var completion)) completion.TrySetException(new WorkflowRpcException(code));
        try { Disconnected?.Invoke(); } catch { }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        Fail("rpc_disposed");
        var child = process;
        if (child is not null)
        {
            try { child.StandardInput.Close(); } catch { }
            try { if (!HasExited(child)) await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); } catch { }
            try { if (!HasExited(child)) child.Kill(entireProcessTree: false); } catch { }
            try { if (!HasExited(child)) await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); } catch { }
        }
        try { await Task.WhenAll(stdoutPump ?? Task.CompletedTask, stderrPump ?? Task.CompletedTask).WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); } catch { }
        child?.Dispose();
        // In-flight request callbacks may still be observing cancellation; do not dispose their gates underneath them.
    }
}

public sealed class WorkflowRpcException(string code) : Exception(code)
{
    public string Code { get; } = code;
}
