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
  private int _nextId = 1;
  private bool _useQueue;
  private Action<string>? _notificationHandler;

  public LspJsonRpcTransport(Stream input, Stream output, TimeSpan timeout)
  {
    _input = input ?? throw new ArgumentNullException(nameof(input));
    _output = output ?? throw new ArgumentNullException(nameof(output));
    _timeout = timeout;
  }

  /// <summary>Registers a handler for server-to-client notifications (e.g. publishDiagnostics).</summary>
  public void OnNotification(Action<string> handler) => _notificationHandler = handler ?? throw new ArgumentNullException(nameof(handler));

  /// <summary>Test hook: queue a raw incoming message instead of reading the stream.</summary>
  public void EnqueueIncomingForTest(string json)
  {
    _useQueue = true;
    _incoming.Enqueue(json);
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
      return _incoming.Count > 0 ? _incoming.Dequeue() : null;
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
  /// server-to-client requests with method-not-found and skipping notifications.</summary>
  public async Task<string?> ReadResponseAsync(CancellationToken ct = default)
  {
    while (true)
    {
      string? msg = await ReadMessageAsync(ct).ConfigureAwait(false);
      if (msg is null) return null;
      using JsonDocument doc = JsonDocument.Parse(msg);
      JsonElement root = doc.RootElement;
      bool hasMethod = root.TryGetProperty("method", out JsonElement _);
      bool hasId = root.TryGetProperty("id", out JsonElement idEl);
      if (hasMethod && hasId)
      {
        string reply = "{\"jsonrpc\":\"2.0\",\"id\":" + idEl.GetRawText() + ",\"error\":{\"code\":-32601,\"message\":\"method not found\"}}";
        await WriteMessageAsync(reply, ct).ConfigureAwait(false);
        continue;
      }
      if (hasMethod && !hasId)
      {
        _notificationHandler?.Invoke(msg);
        continue;
      }
      return msg;
    }
  }

  public async Task<LspRequestResult> SendRequestAsync(string method, string paramsJson, CancellationToken ct = default)
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
