using System.Globalization;
using System.Text;
using System.Text.Json;

namespace eThangAgent.Lsp.ACL;

/// <summary>Outcome of one JSON-RPC request: success with raw result JSON, or a
/// labeled failure (Timeout / Unsupported / Error). Never blocks indefinitely.</summary>
public sealed record LspRequestResult(bool IsSuccess, string? ResultJson, string? ErrorCode, string? ErrorMessage)
{
  public static LspRequestResult Success(string json) => new(true, json, null, null);
  public static LspRequestResult Timeout(string method) => new(false, null, "Timeout", "server did not respond to " + method);
  public static LspRequestResult Unsupported(string method) => new(false, null, "Unsupported", "capability not advertised for " + method);
  public static LspRequestResult Error(string code, string message) => new(false, null, code, message);
}

/// <summary>JSON-RPC framing over stdio (issue #353): Content-Length headers,
/// request ids, responses, notifications, and server-to-client requests answered
/// with method-not-found (-32601) rather than a hang. The session continues after
/// an unimplemented server request.</summary>
public sealed class LspJsonRpcTransport
{
  private readonly Stream _input;
  private readonly Stream _output;
  private readonly TimeSpan _timeout;
  private readonly SemaphoreSlim _writeLock = new(1, 1);
  private readonly Queue<string> _incoming = new();
  private readonly HashSet<string> _registrations = [with(StringComparer.Ordinal)];
  private readonly System.Collections.Concurrent.ConcurrentDictionary<int, TaskCompletionSource<string>> _pending = new();
  private readonly Lock _readerLock = new();
  private readonly CancellationTokenSource _readerCts;
  private Task? _reader;
  private volatile bool _closed;
  private int _nextId = 1;
  private bool _useQueue;
  private Action<string>? _notificationHandler;

  public LspJsonRpcTransport(Stream input, Stream output, TimeSpan timeout)
  {
    _input = input ?? throw new ArgumentNullException(nameof(input));
    _output = output ?? throw new ArgumentNullException(nameof(output));
    _timeout = timeout;
    _readerCts = new CancellationTokenSource();
  }

  /// <summary>The methods the server registered dynamically after initialize. A server
  /// that registers textDocument/diagnostic here advertises no static diagnosticProvider,
  /// so the registration is the only evidence the method is available.</summary>
  public IReadOnlyCollection<string> DynamicRegistrations
  {
    get { lock (_registrations) { return [.. _registrations]; } }
  }

  /// <summary>Registers a handler for server-to-client notifications (e.g. publishDiagnostics).</summary>
  public void OnNotification(Action<string> handler) => _notificationHandler = handler ?? throw new ArgumentNullException(nameof(handler));

  /// <summary>Test hook: queue a raw incoming message instead of reading the stream.</summary>
  public void EnqueueIncomingForTest(string json)
  {
    _useQueue = true;
    lock (_incoming) { _incoming.Enqueue(json); }
  }

  public async Task WriteMessageAsync(string json, CancellationToken ct = default)
  {
    byte[] body = Encoding.UTF8.GetBytes(json);
    string header = "Content-Length: " + body.Length.ToString(CultureInfo.InvariantCulture) + "\r\n\r\n";
    byte[] headerBytes = Encoding.ASCII.GetBytes(header);
    await _writeLock.WaitAsync(ct).ConfigureAwait(false);
    try
    {
      await _output.WriteAsync(headerBytes, ct).ConfigureAwait(false);
      await _output.WriteAsync(body, ct).ConfigureAwait(false);
      await _output.FlushAsync(ct).ConfigureAwait(false);
    }
    finally { _ = _writeLock.Release(); }
  }

  public async Task<string?> ReadMessageAsync(CancellationToken ct = default)
  {
    if (_useQueue)
    {
      lock (_incoming) { return _incoming.Count > 0 ? _incoming.Dequeue() : null; }
    }
    string? header = await ReadHeaderAsync(ct).ConfigureAwait(false);
    if (header is null) return null;
    int length = ParseContentLength(header);
    byte[] body = new byte[length];
    int read = 0;
    while (read < length)
    {
      int n = await _input.ReadAsync(body.AsMemory(read, length - read), ct).ConfigureAwait(false);
      if (n == 0) return null;
      read += n;
    }
    return Encoding.UTF8.GetString(body);
  }

  private async Task<string?> ReadHeaderAsync(CancellationToken ct)
  {
    StringBuilder sb = new();
    byte[] buf = new byte[1];
    while (true)
    {
      int n = await _input.ReadAsync(buf, ct).ConfigureAwait(false);
      if (n == 0) return sb.Length == 0 ? null : sb.ToString();
      sb.Append((char)buf[0]);
      if (sb.Length >= 4 && sb.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal)) return sb.ToString();
      if (sb.Length > 8192) throw new IOException("header too large");
    }
  }

  private static int ParseContentLength(string header)
  {
    foreach (string line in header.Split("\r\n"))
    {
      if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
      {
        return int.Parse(line.Substring("Content-Length:".Length).Trim(), CultureInfo.InvariantCulture);
      }
    }
    throw new IOException("missing Content-Length");
  }

  /// <summary>Reads until a response (id + result/error) arrives, answering
  /// server-to-client requests and skipping notifications.</summary>
  public async Task<string?> ReadResponseAsync(CancellationToken ct = default)
  {
    while (true)
    {
      string? msg = await ReadMessageAsync(ct).ConfigureAwait(false);
      if (msg is null) return null;
      string? reply = BuildServerRequestReply(msg);
      if (reply is not null)
      {
        await WriteMessageAsync(reply, ct).ConfigureAwait(false);
        continue;
      }
      using JsonDocument doc = JsonDocument.Parse(msg);
      JsonElement root = doc.RootElement;
      if (root.TryGetProperty("method", out JsonElement _))
      {
        _notificationHandler?.Invoke(msg);
        continue;
      }
      return msg;
    }
  }

  /// <summary>Answers a server-to-client request, or null when the message is not one.
  /// workspace/configuration is answered with one null per requested item; every other
  /// method with a null result, except a method this client does not know, which keeps
  /// the JSON-RPC method-not-found reply.</summary>
  private string? BuildServerRequestReply(string msg)
  {
    using JsonDocument doc = JsonDocument.Parse(msg);
    JsonElement root = doc.RootElement;
    if (!root.TryGetProperty("method", out JsonElement methodEl)) return null;
    if (!root.TryGetProperty("id", out JsonElement idEl)) return null;
    string method = methodEl.GetString() ?? string.Empty;
    string id = idEl.GetRawText();
    if (string.Equals(method, "workspace/configuration", StringComparison.Ordinal))
    {
      int items = root.TryGetProperty("params", out JsonElement p)
          && p.TryGetProperty("items", out JsonElement array)
          && array.ValueKind == JsonValueKind.Array
              ? array.GetArrayLength()
              : 0;
      string nulls = string.Join(",", Enumerable.Repeat("null", items));
      return "{\"jsonrpc\":\"2.0\",\"id\":" + id + ",\"result\":[" + nulls + "]}";
    }
    if (string.Equals(method, "client/registerCapability", StringComparison.Ordinal))
    {
      RecordRegistrations(root);
      return "{\"jsonrpc\":\"2.0\",\"id\":" + id + ",\"result\":null}";
    }
    if (string.Equals(method, "client/unregisterCapability", StringComparison.Ordinal)
        || string.Equals(method, "window/workDoneProgress/create", StringComparison.Ordinal)
        || string.Equals(method, "workspace/workspaceFolders", StringComparison.Ordinal))
    {
      return "{\"jsonrpc\":\"2.0\",\"id\":" + id + ",\"result\":null}";
    }
    return "{\"jsonrpc\":\"2.0\",\"id\":" + id + ",\"error\":{\"code\":-32601,\"message\":\"method not found\"}}";
  }

  private void RecordRegistrations(JsonElement root)
  {
    if (!root.TryGetProperty("params", out JsonElement p)) return;
    if (!p.TryGetProperty("registrations", out JsonElement regs)) return;
    if (regs.ValueKind != JsonValueKind.Array) return;
    lock (_registrations)
    {
      foreach (JsonElement reg in regs.EnumerateArray())
      {
        if (reg.TryGetProperty("method", out JsonElement m) && m.GetString() is string name)
        {
          _ = _registrations.Add(name);
        }
      }
    }
  }

  /// <summary>Starts the single reader that owns the input stream for the rest of the
  /// session. Every inbound message is dispatched from here: responses complete the
  /// request that is waiting on them, server requests are answered, notifications reach
  /// the handler. A server registers capabilities and publishes diagnostics at times of
  /// its choosing, so reading only while a request is in flight misses both.</summary>
  public void StartReader(CancellationToken ct = default)
  {
    lock (_readerLock)
    {
      _reader ??= Task.Run(() => ReaderLoopAsync(ct), CancellationToken.None);
    }
  }

  /// <summary>Stops the reader and fails every request still waiting on a response.</summary>
  public void Close()
  {
    if (_closed) return;
    _closed = true;
    _readerCts.Cancel();
    foreach (KeyValuePair<int, TaskCompletionSource<string>> entry in _pending)
    {
      _ = entry.Value.TrySetResult(string.Empty);
    }
    _pending.Clear();
  }

  /// <summary>Starts the reader once, on the first request that needs one. A transport in
  /// queue mode is driven inline by <see cref="ReadResponseAsync"/> instead, so the queued
  /// messages a test supplies are consumed in the order the test supplies them.</summary>
  private void EnsureReaderStarted()
  {
    if (!_useQueue)
    {
      StartReader();
    }
  }

  private async Task ReaderLoopAsync(CancellationToken ct)
  {
    using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _readerCts.Token);
    while (!_closed && !linked.IsCancellationRequested)
    {
      string? msg;
      try
      {
        msg = await ReadMessageAsync(linked.Token).ConfigureAwait(false);
      }
      catch (OperationCanceledException)
      {
        return;
      }
      catch (IOException)
      {
        return;
      }
      if (msg is null) return;
      try
      {
        await DispatchAsync(msg, linked.Token).ConfigureAwait(false);
      }
      catch (JsonException)
      {
      }
      catch (OperationCanceledException)
      {
        return;
      }
      catch (IOException)
      {
        return;
      }
    }
  }

  private async Task DispatchAsync(string msg, CancellationToken ct)
  {
    string? reply = BuildServerRequestReply(msg);
    if (reply is not null)
    {
      await WriteMessageAsync(reply, ct).ConfigureAwait(false);
      return;
    }
    using JsonDocument doc = JsonDocument.Parse(msg);
    JsonElement root = doc.RootElement;
    if (root.TryGetProperty("method", out JsonElement _))
    {
      _notificationHandler?.Invoke(msg);
      return;
    }
    if (root.TryGetProperty("id", out JsonElement idEl)
        && idEl.ValueKind == JsonValueKind.Number
        && idEl.TryGetInt32(out int id)
        && _pending.TryRemove(id, out TaskCompletionSource<string>? waiter))
    {
      waiter.TrySetResult(msg);
    }
  }

  public async Task<LspRequestResult> SendRequestAsync(string method, string paramsJson, CancellationToken ct = default)
  {
    if (_useQueue)
    {
      return await SendRequestInlineAsync(method, paramsJson, ct).ConfigureAwait(false);
    }
    if (_closed)
    {
      return LspRequestResult.Error("Closed", "the language server connection is closed");
    }
    EnsureReaderStarted();
    int id = _nextId++;
    string request = "{\"jsonrpc\":\"2.0\",\"id\":" + id.ToString(CultureInfo.InvariantCulture) + ",\"method\":\"" + method + "\",\"params\":" + paramsJson + "}";
    TaskCompletionSource<string> waiter = new(TaskCreationOptions.RunContinuationsAsynchronously);
    _pending[id] = waiter;
    await WriteMessageAsync(request, ct).ConfigureAwait(false);
    using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
    cts.CancelAfter(_timeout);
    try
    {
      string? response;
      try
      {
        response = await waiter.Task.WaitAsync(cts.Token).ConfigureAwait(false);
      }
      catch (OperationCanceledException) when (!ct.IsCancellationRequested)
      {
        return LspRequestResult.Timeout(method);
      }
      if (response is null) return LspRequestResult.Timeout(method);
      using JsonDocument doc = JsonDocument.Parse(response);
      JsonElement root = doc.RootElement;
      if (root.TryGetProperty("error", out JsonElement err))
      {
        string code = err.TryGetProperty("code", out JsonElement c) ? c.GetRawText() : "Error";
        string message = err.TryGetProperty("message", out JsonElement m) ? m.GetString() ?? "" : "";
        return LspRequestResult.Error(code, message);
      }
      string result = root.TryGetProperty("result", out JsonElement r) ? r.GetRawText() : "null";
      return LspRequestResult.Success(result);
    }
    finally
    {
      _ = _pending.TryRemove(id, out _);
    }
  }

  private async Task<LspRequestResult> SendRequestInlineAsync(string method, string paramsJson, CancellationToken ct)
  {
    int id = _nextId++;
    string request = "{\"jsonrpc\":\"2.0\",\"id\":" + id.ToString(CultureInfo.InvariantCulture) + ",\"method\":\"" + method + "\",\"params\":" + paramsJson + "}";
    await WriteMessageAsync(request, ct).ConfigureAwait(false);
    using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
    cts.CancelAfter(_timeout);
    try
    {
      string? response = await ReadResponseAsync(cts.Token).ConfigureAwait(false);
      if (response is null) return LspRequestResult.Timeout(method);
      using JsonDocument doc = JsonDocument.Parse(response);
      JsonElement root = doc.RootElement;
      if (root.TryGetProperty("error", out JsonElement err))
      {
        string code = err.TryGetProperty("code", out JsonElement c) ? c.GetRawText() : "Error";
        string message = err.TryGetProperty("message", out JsonElement m) ? m.GetString() ?? "" : "";
        return LspRequestResult.Error(code, message);
      }
      string result = root.TryGetProperty("result", out JsonElement r) ? r.GetRawText() : "null";
      return LspRequestResult.Success(result);
    }
    catch (OperationCanceledException) when (!ct.IsCancellationRequested)
    {
      return LspRequestResult.Timeout(method);
    }
  }

  public async Task SendNotificationAsync(string method, string paramsJson, CancellationToken ct = default)
  {
    string notif = "{\"jsonrpc\":\"2.0\",\"method\":\"" + method + "\",\"params\":" + paramsJson + "}";
    await WriteMessageAsync(notif, ct).ConfigureAwait(false);
  }
}
