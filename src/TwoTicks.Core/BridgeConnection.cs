using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace TwoTicks.Core;

/// <summary>A request the helper answered with an error.</summary>
public sealed class BridgeException(string message) : Exception(message);

/// <summary>
/// The line protocol with the helper process: requests go to its standard input
/// as one JSON object per line, and responses and events come back the same way
/// on its standard output.
/// </summary>
internal sealed class BridgeConnection : IDisposable
{
    private readonly ConcurrentDictionary<long, TaskCompletionSource<JsonElement>> _pending = new();
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly Process _process;
    private readonly StreamWriter _input;
    private long _nextId;
    private bool _exited;

    /// <summary>Raised on a background thread for every event line.</summary>
    public event Action<string, JsonElement>? EventReceived;

    /// <summary>Raised on a background thread once the process has ended.</summary>
    public event Action? Exited;

    public Process Process => _process;

    private BridgeConnection(Process process)
    {
        _process = process;
        _input = new StreamWriter(process.StandardInput.BaseStream, new UTF8Encoding(false)) { AutoFlush = false, NewLine = "\n" };
    }

    public static BridgeConnection Start(string executable, string dataFolder, bool debug)
    {
        var info = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            CreateNoWindow = true,
        };
        info.ArgumentList.Add("--data");
        info.ArgumentList.Add(dataFolder);
        // For the text it writes itself, such as "Missed voice call".
        info.ArgumentList.Add("--lang");
        info.ArgumentList.Add(Loc.Language);
        if (debug)
        {
            info.ArgumentList.Add("--debug");
        }

        var process = Process.Start(info) ?? throw new BridgeException(Loc.T("helper.didNotStart"));
        var connection = new BridgeConnection(process);
        _ = Task.Run(connection.ReadLoopAsync);
        // Anything the helper prints to standard error is a crash report; the log
        // has the rest. Drained so a full pipe never blocks it.
        _ = Task.Run(() => process.StandardError.ReadToEndAsync());
        return connection;
    }

    private async Task ReadLoopAsync()
    {
        StreamReader output = _process.StandardOutput;
        try
        {
            while (await output.ReadLineAsync().ConfigureAwait(false) is { } line)
            {
                if (line.Length == 0)
                {
                    continue;
                }
                Dispatch(line);
            }
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException)
        {
            // The pipe broke because the process ended.
        }

        _exited = true;
        foreach (var pending in _pending.Values)
        {
            pending.TrySetException(new BridgeException(Loc.T("helper.stopped")));
        }
        _pending.Clear();
        Exited?.Invoke();
    }

    private void Dispatch(string line)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(line);
        }
        catch (JsonException)
        {
            return;
        }

        using (document)
        {
            JsonElement root = document.RootElement;
            if (root.TryGetProperty("event", out JsonElement name))
            {
                JsonElement data = root.TryGetProperty("data", out JsonElement d) ? d.Clone() : default;
                EventReceived?.Invoke(name.GetString() ?? "", data);
                return;
            }

            if (!root.TryGetProperty("id", out JsonElement idElement) || !_pending.TryRemove(idElement.GetInt64(), out var pending))
            {
                return;
            }
            if (root.TryGetProperty("error", out JsonElement error) && error.ValueKind == JsonValueKind.String)
            {
                pending.TrySetException(new BridgeException(error.GetString() ?? Loc.T("helper.unknownError")));
            }
            else
            {
                pending.TrySetResult(root.TryGetProperty("result", out JsonElement result) ? result.Clone() : default);
            }
        }
    }

    public async Task<JsonElement> CallAsync(string method, JsonObject? parameters, CancellationToken cancellationToken = default)
    {
        if (_exited)
        {
            throw new BridgeException(Loc.T("helper.notRunning"));
        }

        long id = Interlocked.Increment(ref _nextId);
        var completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = completion;

        var request = new JsonObject { ["id"] = id, ["method"] = method };
        if (parameters is not null)
        {
            request["params"] = parameters;
        }
        string line = request.ToJsonString();

        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _input.WriteLineAsync(line).ConfigureAwait(false);
            await _input.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (IOException)
        {
            _pending.TryRemove(id, out _);
            throw new BridgeException(Loc.T("helper.notRunning"));
        }
        finally
        {
            _writeLock.Release();
        }

        using (cancellationToken.Register(() =>
        {
            if (_pending.TryRemove(id, out var canceled))
            {
                canceled.TrySetCanceled(cancellationToken);
            }
        }))
        {
            return await completion.Task.ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Closes the helper's input, which tells it to disconnect and exit, and kills
    /// it if it has not done so within a few seconds.
    /// </summary>
    public void Dispose()
    {
        try
        {
            _input.Dispose();
        }
        catch (IOException)
        {
        }

        try
        {
            if (!_process.WaitForExit(3000))
            {
                _process.Kill();
            }
        }
        catch (InvalidOperationException)
        {
        }
        _process.Dispose();
    }
}
