using System.Collections.Concurrent;
using System.Globalization;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using eThangAgent.ToolDomain.FileClaims;
using eThangAgent.ToolDomain.Lsp;

namespace eThangAgent.Lsp.ACL;

public static class LspProtocol
{
  public const int ProtocolVersion = 1;
  public const int MaxFrameBytes = 4 * 1024 * 1024;
}

public sealed class LspPipeClient : IDisposable
{
  private readonly NamedPipeClientStream _pipe;
  private readonly SemaphoreSlim _writeLock = new(1, 1);
  private readonly ConcurrentDictionary<int, TaskCompletionSource<JsonObject>> _pending = new();
  private readonly CancellationTokenSource _readerCts = new();

  /// <summary>Bytes read past the last newline. A single pipe read can carry the tail of
  ///     one frame and the whole of the next, so the remainder is kept here instead of
  ///     being dropped: a dropped tail desynchronizes the stream for every later reply.</summary>
  private readonly List<byte> _carry = [];

  private Task? _reader;

  private LspPipeClient(NamedPipeClientStream pipe) => _pipe = pipe;

  public string WorkspaceId { get; private set; } = string.Empty;
  public int ProtocolVersion { get; private set; }

  private volatile bool _broken;
  private int _disposed;

  /// <summary>True once this client can no longer reach its daemon: the server has hung
  ///     up, or a write or read on the pipe has failed. The owner discards a broken client
  ///     and reconnects instead of reusing it.</summary>
  public bool IsBroken
  {
    get
    {
      if (_broken)
      {
        return true;
      }

      try
      {
        return !_pipe.IsConnected;
      }
      catch (ObjectDisposedException)
      {
        return true;
      }
    }
  }

  public static async Task<LspPipeClient> ConnectAsync(string pipeName, string workspaceId, string appInstanceId, CancellationToken ct = default)
  {
    NamedPipeClientStream pipe = new(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
    await pipe.ConnectAsync(ct).ConfigureAwait(false);
    LspPipeClient client = new(pipe);
    try
    {
      await client.HandshakeAsync(workspaceId, appInstanceId, ct).ConfigureAwait(false);
      client.StartReader();
      return client;
    }
    catch
    {
      client.Dispose();
      throw;
    }
  }

  private async Task HandshakeAsync(string workspaceId, string appInstanceId, CancellationToken ct)
  {
    JsonObject hello = new()
    {
      ["id"] = 1,
      ["method"] = "hello",
      ["params"] = new JsonObject
      {
        ["protocolVersion"] = LspProtocol.ProtocolVersion,
        ["workspaceId"] = workspaceId,
        ["appInstanceId"] = appInstanceId
      }
    };
    await WriteLineAsync(hello.ToJsonString(), ct).ConfigureAwait(false);
    string line = await ReadLineAsync(ct).ConfigureAwait(false)
        ?? throw new IOException("daemon closed during hello");
    using JsonDocument doc = JsonDocument.Parse(line);
    JsonElement root = doc.RootElement;
    if (root.TryGetProperty("error", out JsonElement err))
    {
      string code = err.TryGetProperty("code", out JsonElement c) ? c.GetString() ?? "unknown" : "unknown";
      if (code == "workspace-mismatch") throw new LspWorkspaceMismatchException("workspace-mismatch");
      throw new InvalidOperationException($"hello error: {code}");
    }
    JsonElement result = root.GetProperty("result");
    WorkspaceId = result.GetProperty("workspaceId").GetString() ?? string.Empty;
    ProtocolVersion = result.GetProperty("protocolVersion").GetInt32();
  }

  private async Task WriteLineAsync(string json, CancellationToken ct)
  {
    byte[] bytes = Encoding.UTF8.GetBytes(json + "\n");
    await _writeLock.WaitAsync(ct).ConfigureAwait(false);
    try
    {
      await _pipe.WriteAsync(bytes, ct).ConfigureAwait(false);
      await _pipe.FlushAsync(ct).ConfigureAwait(false);
    }
    finally
    {
      _ = _writeLock.Release();
    }
  }

  /// <summary>Reads one newline-terminated frame, keeping any bytes that follow the
  ///     newline for the next call. Returns null when the daemon closed the pipe. Only the
  ///     handshake and the reader loop call this, and the handshake finishes before the loop
  ///     starts, so the carry buffer is never entered concurrently.</summary>
  private async Task<string?> ReadLineAsync(CancellationToken ct)
  {
    byte[] buffer = new byte[8192];
    while (true)
    {
      int newline = _carry.IndexOf((byte)'\n');
      if (newline >= 0)
      {
        string line = Encoding.UTF8.GetString([.. _carry.Take(newline)]);
        _carry.RemoveRange(0, newline + 1);
        return line;
      }

      if (_carry.Count > LspProtocol.MaxFrameBytes) throw new IOException("frame too large");
      int read = await _pipe.ReadAsync(buffer, ct).ConfigureAwait(false);
      if (read == 0) return null;
      _carry.AddRange(buffer.AsSpan(0, read).ToArray());
    }
  }

  private int _nextRequestId = 2;

  /// <summary>How long one request may wait for its reply before the caller is told it
  ///     timed out. Independent of the caller's own budget, so a request that outlives its
  ///     caller cannot hold a pending entry for the life of the connection.</summary>
  private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(30);

  /// <summary>Reads frames for the life of the connection and completes the request each
  ///     reply belongs to. Replies are matched by id, so a reply that arrives while another
  ///     request is in flight - or two replies coalesced into one pipe read - reaches the
  ///     right caller instead of being consumed in write order.</summary>
  private void StartReader() => _reader ??= Task.Run(ReadLoopAsync, CancellationToken.None);

  private async Task ReadLoopAsync()
  {
    try
    {
      while (true)
      {
        string? line = await ReadLineAsync(_readerCts.Token).ConfigureAwait(false);
        if (line is null)
        {
          break;
        }

        Dispatch(line);
      }
    }
    catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException)
    {
    }

    _broken = true;
    FailAllPending();
  }

  private void Dispatch(string line)
  {
    JsonObject reply;
    try
    {
      reply = JsonNode.Parse(line)?.AsObject() ?? [];
    }
    catch (JsonException)
    {
      return;
    }

    if (reply["id"] is JsonValue idValue && idValue.TryGetValue(out int id)
        && _pending.TryRemove(id, out TaskCompletionSource<JsonObject>? waiter))
    {
      _ = waiter.TrySetResult(reply);
    }
  }

  private void FailAllPending()
  {
    foreach (KeyValuePair<int, TaskCompletionSource<JsonObject>> entry in _pending)
    {
      _ = entry.Value.TrySetException(new IOException("the daemon connection was lost"));
    }

    _pending.Clear();
  }

  private async Task<JsonObject> RequestAsync(string method, JsonObject parameters, CancellationToken ct)
  {
    if (_broken)
    {
      throw new IOException("the daemon connection is broken");
    }

    int id = Interlocked.Increment(ref _nextRequestId);
    JsonObject request = new() { ["id"] = id, ["method"] = method, ["params"] = parameters };
    TaskCompletionSource<JsonObject> waiter = new(TaskCreationOptions.RunContinuationsAsynchronously);
    _pending[id] = waiter;
    try
    {
      await WriteLineAsync(request.ToJsonString(), ct).ConfigureAwait(false);
      JsonObject reply;
      using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
      deadline.CancelAfter(RequestTimeout);
      try
      {
        reply = await waiter.Task.WaitAsync(deadline.Token).ConfigureAwait(false);
      }
      catch (OperationCanceledException) when (!ct.IsCancellationRequested)
      {
        throw new TimeoutException(method + " timed out");
      }

      if (reply["error"] is JsonObject error)
      {
        string code = error["code"]?.ToString() ?? "unknown";
        throw new InvalidOperationException(method + " error: " + code);
      }

      return reply["result"]?.AsObject() ?? [];
    }
    finally
    {
      _ = _pending.TryRemove(id, out _);
    }
  }

  private static FileClaimResult MapClaim(JsonObject result)
  {
    bool granted = result["granted"]?.GetValue<bool>() ?? false;
    string? holder = result["holderSessionId"]?.GetValue<string>();
    DateTimeOffset? expiry = result["expiry"] is JsonValue ev && ev.TryGetValue(out string? s) && DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out DateTimeOffset parsed) ? parsed : null;
    return new FileClaimResult(granted, holder, expiry);
  }

  /// <summary>Acquires a file claim in the daemon's shared store (issue #363).</summary>
  public async Task<FileClaimResult> AcquireClaimAsync(string path, string sessionId, CancellationToken ct = default)
  {
    JsonObject result = await RequestAsync("file-claim.acquire", new JsonObject { ["path"] = path, ["sessionId"] = sessionId }, ct).ConfigureAwait(false);
    return MapClaim(result);
  }

  /// <summary>Renews the caller's own file claim in the daemon store (issue #363).</summary>
  public async Task<FileClaimResult> RenewClaimAsync(string path, string sessionId, CancellationToken ct = default)
  {
    JsonObject result = await RequestAsync("file-claim.renew", new JsonObject { ["path"] = path, ["sessionId"] = sessionId }, ct).ConfigureAwait(false);
    return MapClaim(result);
  }

  /// <summary>Releases the caller's own file claim in the daemon store (issue #363).</summary>
  public async Task<bool> ReleaseClaimAsync(string path, string sessionId, CancellationToken ct = default)
  {
    JsonObject result = await RequestAsync("file-claim.release", new JsonObject { ["path"] = path, ["sessionId"] = sessionId }, ct).ConfigureAwait(false);
    return result["released"]?.GetValue<bool>() ?? false;
  }

  /// <summary>Queries advisory diagnostics for one file from the daemon.</summary>
  public async Task<AdvisoryDiagnosticsResult> GetDiagnosticsAsync(string path, CancellationToken ct = default)
  {
    ArgumentException.ThrowIfNullOrWhiteSpace(path);
    JsonObject result = await RequestAsync("lsp.diagnostics", new JsonObject { ["path"] = path }, ct).ConfigureAwait(false);
    string stateText = result["state"]?.GetValue<string>() ?? nameof(AdvisoryDiagnosticsState.Unavailable);
    AdvisoryDiagnosticsState state = Enum.TryParse(stateText, out AdvisoryDiagnosticsState parsed)
      ? parsed
      : AdvisoryDiagnosticsState.Unavailable;
    List<AdvisoryDiagnostic> diagnostics = [];
    if (result["diagnostics"] is JsonArray arr)
    {
      foreach (JsonNode? node in arr)
      {
        JsonObject d = node!.AsObject();
        string? code = d["code"]?.GetValue<string>();
        diagnostics.Add(new AdvisoryDiagnostic(
          d["startLine"]!.GetValue<int>(), d["startCharacter"]!.GetValue<int>(),
          d["endLine"]!.GetValue<int>(), d["endCharacter"]!.GetValue<int>(),
          code, d["message"]?.GetValue<string>() ?? string.Empty));
      }
    }
    return new AdvisoryDiagnosticsResult(state, diagnostics, LanguageId: result["languageId"]?.GetValue<string>());
  }

  /// <summary>Issues one lsp.symbol query and returns the daemon's answer.</summary>
  public async Task<LspSymbolResult> SymbolAsync(string action, string uri, string symbolName, int occurrence, int depth, string newName = "", CancellationToken ct = default)
  {
    ArgumentException.ThrowIfNullOrWhiteSpace(action);
    JsonObject parameters = new()
    {
      ["action"] = action,
      ["uri"] = uri,
      ["symbolName"] = symbolName,
      ["occurrence"] = occurrence,
      ["depth"] = depth,
      ["newName"] = newName,
    };
    JsonObject result = await RequestAsync("lsp.symbol", parameters, ct).ConfigureAwait(false);
    return MapSymbol(result);
  }

  private static LspSymbolResult MapSymbol(JsonObject result)
  {
    string stateText = result["state"]?.GetValue<string>() ?? nameof(LspSymbolState.Unavailable);
    LspSymbolState state = Enum.TryParse(stateText, out LspSymbolState parsed) ? parsed : LspSymbolState.Unavailable;
    List<LspSymbolSite> sites = [];
    if (result["sites"] is JsonArray arr)
    {
      foreach (JsonNode? node in arr)
      {
        JsonObject site = node!.AsObject();
        sites.Add(new LspSymbolSite(
            site["uri"]?.GetValue<string>() ?? string.Empty,
            site["line"]?.GetValue<int>() ?? 0,
            site["character"]?.GetValue<int>() ?? 0,
            site["symbolName"]?.GetValue<string>() ?? string.Empty));
      }
    }
    return new LspSymbolResult(
        state,
        sites,
        result["occurrenceUsed"]?.GetValue<int>() ?? 0,
        result["totalOccurrences"]?.GetValue<int>() ?? 1,
        result["message"]?.GetValue<string>(),
        result["languageId"]?.GetValue<string>());
  }

  /// <summary>Asks the daemon to stop when this is its last client. Answers true when the
  ///     daemon accepted and is exiting, false when other hosts still hold it. A daemon that
  ///     cannot answer is already gone, which is the outcome the caller wanted.</summary>
  public async Task<bool> ShutdownIfIdleAsync(CancellationToken ct = default)
  {
    try
    {
      JsonObject result = await RequestAsync("daemon.shutdown-if-idle", [], ct).ConfigureAwait(false);
      return result["shutdown"]?.GetValue<bool>() ?? false;
    }
    catch (Exception ex) when (ex is IOException or ObjectDisposedException or TimeoutException or InvalidOperationException)
    {
      return false;
    }
  }

  /// <summary>Sends one watcher.announce notification over the pipe.</summary>
  public async Task SendWatcherAnnouncementAsync(WatcherAnnouncement announcement, CancellationToken ct = default)
  {
    ArgumentNullException.ThrowIfNull(announcement);
    await WriteLineAsync(WatcherAnnouncementCodec.Encode(announcement), ct).ConfigureAwait(false);
  }

  public void Dispose()
  {
    if (Interlocked.Exchange(ref _disposed, 1) != 0)
    {
      return;
    }

    _broken = true;
    _readerCts.Cancel();
    FailAllPending();
    _pipe.Dispose();
    _writeLock.Dispose();
    _readerCts.Dispose();
  }
}

public sealed class LspDaemonServer : IDisposable
{
  private readonly string _pipeName;
  private readonly string _workspaceId;
  private readonly Func<Task> _onConnected;
  private readonly string _daemonInstanceId = Guid.NewGuid().ToString("N");
  private LspDocumentSync? _forwarder;
  private LspServerSet? _servers;
  private Func<CancellationToken, Task<LspServerSet>>? _serversProvider;
  private Task<LspServerSet>? _startingServers;
  private LspDiagnosticsService? _diagnosticsService;
  private LspSymbolService? _symbolService;
  private readonly IFileClaimStore _claimStore;
  private readonly ConcurrentDictionary<NamedPipeServerStream, byte> _connections = new();
  private Func<CancellationToken, Task>? _restartHook;
  private readonly CancellationTokenSource _shutdownCts = new();

  /// <summary>Cancelled when the last client asks the daemon to stop. A workspace's daemon
  ///     serves every host in that workspace, so it outlives the session that happened to
  ///     start it - otherwise one session's exit destroys the warm server every sibling is
  ///     using, and the next session pays a full solution load again. The stop is a request
  ///     from the departing client, not a timer: nothing polls for idleness.</summary>
  public CancellationToken ShutdownToken => _shutdownCts.Token;

  public LspDaemonServer(string pipeName, string workspaceId, Func<Task> onConnected, IFileClaimStore? claimStore = null)
  {
    _pipeName = pipeName;
    _workspaceId = workspaceId;
    _onConnected = onConnected;
    _claimStore = claimStore ?? new SharedFileClaimStore();
  }

  public string DaemonInstanceId => _daemonInstanceId;

  /// <summary>Attaches the document-sync forwarder (issue #362): watcher.announce
  /// notifications received on the pipe are applied to it after hello.</summary>
  public void AttachForwarder(LspDocumentSync forwarder) => _forwarder = forwarder ?? throw new ArgumentNullException(nameof(forwarder));

  /// <summary>Attaches the workspace's running servers. Every request is then routed by the
  ///     path it names: a watcher announcement reaches each server for the paths its own
  ///     extensions claim, and a diagnostics or symbol request reaches the server claiming
  ///     that document's extension. A path no server claims is answered empty, never as an
  ///     error.</summary>
  public void AttachServers(LspServerSet servers) => _servers = servers ?? throw new ArgumentNullException(nameof(servers));

  /// <summary>Attaches the workspace's running servers as work in progress, so the daemon
  ///     answers its pipe while the servers are still starting. A language server can take far
  ///     longer to initialize than a caller's connect budget, and a daemon that is unreachable
  ///     until then is a workspace with no language server at all.</summary>
  public void AttachServers(Func<CancellationToken, Task<LspServerSet>> servers) =>
      _serversProvider = servers ?? throw new ArgumentNullException(nameof(servers));

  public void AttachDiagnosticsService(LspDiagnosticsService service) => _diagnosticsService = service ?? throw new ArgumentNullException(nameof(service));

  public void AttachSymbolService(LspSymbolService service) => _symbolService = service ?? throw new ArgumentNullException(nameof(service));

  public void AttachRestartHook(Func<CancellationToken, Task> hook) => _restartHook = hook ?? throw new ArgumentNullException(nameof(hook));

  public async Task ServeOnceAsync(CancellationToken ct = default)
  {
    NamedPipeServerStream server = new(_pipeName, PipeDirection.InOut, 8, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
    try
    {
      await server.WaitForConnectionAsync(ct).ConfigureAwait(false);
      await ServeOneAsync(server, ct).ConfigureAwait(false);
    }
    finally
    {
      await server.DisposeAsync().ConfigureAwait(false);
    }
  }

  /// <summary>Serves connections until the token cancels. Each accepted connection is
  /// handed to its own serve loop, so every host in the workspace reaches the same
  /// daemon store concurrently; a single sequential accept loop would let one long-lived
  /// host starve every sibling.</summary>
  public async Task ServeUntilClosedAsync(CancellationToken ct = default)
  {
    while (!ct.IsCancellationRequested)
    {
      try
      {
        await AcceptAsync(ct).ConfigureAwait(false);
      }
      catch (OperationCanceledException) when (ct.IsCancellationRequested)
      {
        return;
      }
      catch (IOException)
      {
        return;
      }
    }
  }

  private async Task AcceptAsync(CancellationToken ct)
  {
    NamedPipeServerStream server = new(_pipeName, PipeDirection.InOut, 8, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
    try
    {
      await server.WaitForConnectionAsync(ct).ConfigureAwait(false);
    }
    catch (OperationCanceledException) when (ct.IsCancellationRequested)
    {
      await server.DisposeAsync().ConfigureAwait(false);
      return;
    }

    _ = _connections.TryAdd(server, 0);
    _ = Task.Run(() => ServeConnectionLifecycleAsync(server, ct), CancellationToken.None);
  }

  private async Task ServeConnectionLifecycleAsync(NamedPipeServerStream server, CancellationToken ct)
  {
    try
    {
      await ServeOneAsync(server, ct).ConfigureAwait(false);
    }
    catch (OperationCanceledException) when (ct.IsCancellationRequested)
    {
    }
    catch (IOException)
    {
    }
    finally
    {
      _ = _connections.TryRemove(server, out _);
      await server.DisposeAsync().ConfigureAwait(false);
    }
  }

  private async Task ServeOneAsync(NamedPipeServerStream server, CancellationToken ct)
  {
    string line = await ReadLineAsync(server, ct).ConfigureAwait(false);
    using JsonDocument doc = JsonDocument.Parse(line);
    JsonElement p = doc.RootElement.GetProperty("params");
    string clientWs = p.GetProperty("workspaceId").GetString() ?? string.Empty;
    if (!string.Equals(clientWs, _workspaceId, StringComparison.Ordinal))
    {
      JsonObject reply = new() { ["id"] = 1, ["error"] = new JsonObject { ["code"] = "workspace-mismatch", ["message"] = "workspace mismatch" } };
      await WriteLineAsync(server, reply.ToJsonString(), ct).ConfigureAwait(false);
      return;
    }
    JsonObject ack = new()
    {
      ["id"] = 1,
      ["result"] = new JsonObject
      {
        ["protocolVersion"] = LspProtocol.ProtocolVersion,
        ["workspaceId"] = _workspaceId,
        ["daemonPid"] = Environment.ProcessId,
        ["daemonInstanceId"] = _daemonInstanceId
      }
    };
    await WriteLineAsync(server, ack.ToJsonString(), ct).ConfigureAwait(false);
    await _onConnected().ConfigureAwait(false);
    if (_restartHook is not null)
    {
      await _restartHook(ct).ConfigureAwait(false);
    }
    await ServeConnectionAsync(server, ct).ConfigureAwait(false);
  }

  /// <summary>Serves one connected host for the life of the connection: watcher
  /// announcements are forwarded to the language server and every other line is a
  /// request answered from the daemon's own services. ONE loop, because the production
  /// daemon always attaches a forwarder - an announcement-only loop answers no query at
  /// all, and an advisory query then waits out its budget and reports stale.</summary>
  private async Task ServeConnectionAsync(NamedPipeServerStream server, CancellationToken ct)
  {
    List<byte> lineBytes = [];
    byte[] buffer = new byte[8192];
    while (!ct.IsCancellationRequested)
    {
      int read;
      try { read = await server.ReadAsync(buffer, ct).ConfigureAwait(false); }
      catch (OperationCanceledException) { return; }
      catch (IOException) { return; }
      if (read == 0) return;
      for (int i = 0; i < read; i++)
      {
        if (buffer[i] == (byte)'\n')
        {
          string line = Encoding.UTF8.GetString([.. lineBytes]);
          lineBytes.Clear();
          await HandleLineAsync(server, line, ct).ConfigureAwait(false);
        }
        else
        {
          lineBytes.Add(buffer[i]);
          if (lineBytes.Count > LspProtocol.MaxFrameBytes) return;
        }
      }
    }
  }

  private async Task HandleLineAsync(NamedPipeServerStream server, string line, CancellationToken ct)
  {
    WatcherAnnouncement? announcement = WatcherAnnouncementCodec.TryDecode(line);
    if (announcement is not null)
    {
      LspServerSet? servers = await ServersAsync(ct).ConfigureAwait(false);
      if (servers is not null)
      {
        await servers.ApplyAnnouncementAsync(announcement, ct).ConfigureAwait(false);
      }
      else if (_forwarder is not null)
      {
        await _forwarder.ApplyAsync(announcement, ct).ConfigureAwait(false);
      }

      return;
    }

    await HandleSymbolQueryAsync(server, line, ct).ConfigureAwait(false);
  }

  private async Task HandleSymbolQueryAsync(NamedPipeServerStream server, string line, CancellationToken ct)
  {
    try
    {
      using JsonDocument doc = JsonDocument.Parse(line);
      JsonElement root = doc.RootElement;
      if (!root.TryGetProperty("method", out JsonElement m)) return;
      string method = m.GetString() ?? string.Empty;
      if (method.StartsWith("file-claim.", StringComparison.Ordinal))
      {
        await HandleClaimRequestAsync(server, line, method, ct).ConfigureAwait(false);
        return;
      }
      if (method == "lsp.diagnostics")
      {
        await HandleDiagnosticsRequestAsync(server, line, ct).ConfigureAwait(false);
        return;
      }
      if (method == "daemon.shutdown-if-idle")
      {
        await HandleShutdownIfIdleAsync(server, line, ct).ConfigureAwait(false);
        return;
      }
      if (method != "lsp.symbol") return;
      LspServerSet? routing = await ServersAsync(ct).ConfigureAwait(false);
      if (!root.TryGetProperty("id", out JsonElement idEl)) return;
      if (!root.TryGetProperty("params", out JsonElement p)) return;
      string action = p.GetProperty("action").GetString() ?? string.Empty;
      string uri = p.TryGetProperty("uri", out JsonElement u) ? u.GetString() ?? string.Empty : string.Empty;
      string symbolName = p.TryGetProperty("symbolName", out JsonElement s) ? s.GetString() ?? string.Empty : string.Empty;
      int occurrence = p.TryGetProperty("occurrence", out JsonElement o) ? o.GetInt32() : 0;
      int depth = p.TryGetProperty("depth", out JsonElement d) ? d.GetInt32() : 1;
      string newName = p.TryGetProperty("newName", out JsonElement n) ? n.GetString() ?? string.Empty : string.Empty;
      if (routing is not null)
      {
        LspSymbolResult routed = await routing
            .QuerySymbolAsync(action, uri, symbolName, occurrence, depth, newName, ct)
            .ConfigureAwait(false);
        await WriteLineAsync(server, SymbolReply(idEl.GetInt32(), routed).ToJsonString(), ct).ConfigureAwait(false);
        return;
      }

      if (_symbolService is null) return;
      LspSymbolResult result = await _symbolService
          .ExecuteAsync(action, uri, symbolName, occurrence, depth, newName, ct).ConfigureAwait(false);
      await WriteLineAsync(server, SymbolReply(idEl.GetInt32(), result).ToJsonString(), ct).ConfigureAwait(false);
    }
    catch (JsonException) { }
    catch (IOException) { }
  }

  /// <summary>The diagnostics for one path: from the server claiming its extension when the
  ///     daemon holds a set, from the single attached service when it holds only that, and
  ///     Unavailable when it holds neither.</summary>
  private async Task<LspDiagnosticsResult> PullDiagnosticsAsync(string path, string uri, CancellationToken ct)
  {
    LspServerSet? servers = await ServersAsync(ct).ConfigureAwait(false);
    if (servers is not null)
    {
      return await servers.GetDiagnosticsAsync(path, ct).ConfigureAwait(false);
    }

    if (_diagnosticsService is null)
    {
      return new LspDiagnosticsResult(LspDiagnosticsState.Unavailable, []);
    }

    return await _diagnosticsService.PullAsync(uri, ct).ConfigureAwait(false);
  }

  /// <summary>The workspace's running servers, waiting for a start that is still in progress.
  ///     A start that fails leaves no servers, which is the same answer as a workspace that
  ///     needed none: an empty result, never a failed request.</summary>
  private async Task<LspServerSet?> ServersAsync(CancellationToken ct)
  {
    if (_servers is not null)
    {
      return _servers;
    }

    if (_serversProvider is null)
    {
      return null;
    }

    try
    {
      _startingServers ??= _serversProvider(ct);
      _servers = await _startingServers.WaitAsync(ct).ConfigureAwait(false);
      _serversProvider = null;
      return _servers;
    }
    catch (Exception ex) when (ex is IOException or InvalidOperationException or TimeoutException
        or System.ComponentModel.Win32Exception or OperationCanceledException)
    {
      return null;
    }
  }

  /// <summary>The reply one symbol result is sent as.</summary>
  private static JsonObject SymbolReply(int id, LspSymbolResult result) =>
      new()
      {
        ["id"] = id,
        ["result"] = new JsonObject
        {
          ["state"] = result.State.ToString(),
          ["occurrenceUsed"] = result.OccurrenceUsed,
          ["totalOccurrences"] = result.TotalOccurrences,
          ["message"] = result.Message ?? string.Empty,
          ["languageId"] = result.LanguageId ?? string.Empty,
          ["sites"] = new JsonArray(result.Sites.Select(s => (JsonNode)new JsonObject { ["uri"] = s.Uri, ["line"] = s.Line, ["character"] = s.Character, ["symbolName"] = s.SymbolName }).ToArray()),
        },
      };

  private async Task HandleDiagnosticsRequestAsync(NamedPipeServerStream server, string line, CancellationToken ct)
  {
    try
    {
      using JsonDocument doc = JsonDocument.Parse(line);
      JsonElement root = doc.RootElement;
      if (!root.TryGetProperty("id", out JsonElement idEl)) return;
      if (!root.TryGetProperty("params", out JsonElement p)) return;
      string path = p.GetProperty("path").GetString() ?? string.Empty;
      string uri = LspUriMapper.ToUri(path);
      LspDiagnosticsResult result = await PullDiagnosticsAsync(path, uri, ct).ConfigureAwait(false);
      JsonArray diagnostics = [];
      foreach (LspDiagnostic d in result.Diagnostics)
      {
        JsonObject o = new()
        {
          ["startLine"] = d.StartLine + 1,
          ["startCharacter"] = d.StartCharacter,
          ["endLine"] = d.EndLine + 1,
          ["endCharacter"] = d.EndCharacter,
          ["message"] = d.Message,
        };
        if (d.Code is not null) o["code"] = d.Code;
        diagnostics.Add(o);
      }
      JsonObject reply = new()
      {
        ["id"] = idEl.GetInt32(),
        ["result"] = new JsonObject
        {
          ["state"] = result.State.ToString(),
          ["languageId"] = result.LanguageId ?? string.Empty,
          ["diagnostics"] = diagnostics,
        },
      };
      await WriteLineAsync(server, reply.ToJsonString(), ct).ConfigureAwait(false);
    }
    catch (JsonException) { }
    catch (IOException) { }
  }

  /// <summary>Stops the daemon when the asking connection is its only client. The count is
  ///     read before the reply is written, so the answer describes the state the caller is
  ///     leaving behind: one connection means this caller, and nobody is left to serve.</summary>
  private async Task HandleShutdownIfIdleAsync(NamedPipeServerStream server, string line, CancellationToken ct)
  {
    try
    {
      using JsonDocument doc = JsonDocument.Parse(line);
      if (!doc.RootElement.TryGetProperty("id", out JsonElement idEl)) return;
      bool last = _connections.Count <= 1;
      JsonObject reply = new() { ["id"] = idEl.GetInt32(), ["result"] = new JsonObject { ["shutdown"] = last } };
      await WriteLineAsync(server, reply.ToJsonString(), ct).ConfigureAwait(false);
      if (last)
      {
        await _shutdownCts.CancelAsync().ConfigureAwait(false);
      }
    }
    catch (JsonException) { }
    catch (IOException) { }
  }

  private async Task HandleClaimRequestAsync(NamedPipeServerStream server, string line, string method, CancellationToken ct)
  {
    try
    {
      using JsonDocument doc = JsonDocument.Parse(line);
      JsonElement root = doc.RootElement;
      if (!root.TryGetProperty("id", out JsonElement idEl)) return;
      if (!root.TryGetProperty("params", out JsonElement p)) return;
      string path = p.GetProperty("path").GetString() ?? string.Empty;
      string sessionId = p.GetProperty("sessionId").GetString() ?? string.Empty;
      DateTimeOffset now = DateTimeOffset.UtcNow;
      JsonObject result = method switch
      {
        "file-claim.acquire" => ClaimJson(_claimStore.Acquire(_workspaceId, path, sessionId, now)),
        "file-claim.renew" => ClaimJson(_claimStore.Renew(_workspaceId, path, sessionId, now)),
        "file-claim.release" => new JsonObject { ["released"] = _claimStore.Release(_workspaceId, path, sessionId, now) },
        _ => new JsonObject { ["granted"] = false },
      };
      JsonObject reply = new() { ["id"] = idEl.GetInt32(), ["result"] = result };
      await WriteLineAsync(server, reply.ToJsonString(), ct).ConfigureAwait(false);
    }
    catch (JsonException) { }
    catch (IOException) { }
  }

  private static JsonObject ClaimJson(FileClaimResult r)
  {
    JsonObject o = new() { ["granted"] = r.Granted };
    if (r.HolderSessionId is not null) o["holderSessionId"] = r.HolderSessionId;
    if (r.Expiry is not null) o["expiry"] = r.Expiry.Value.ToString("o", CultureInfo.InvariantCulture);
    return o;
  }

  private static async Task<string> ReadLineAsync(NamedPipeServerStream server, CancellationToken ct)
  {
    List<byte> lineBytes = [];
    byte[] buffer = new byte[8192];
    while (true)
    {
      int read = await server.ReadAsync(buffer, ct).ConfigureAwait(false);
      if (read == 0) throw new IOException("client closed");
      for (int i = 0; i < read; i++)
      {
        if (buffer[i] == (byte)'\n')
        {
          return Encoding.UTF8.GetString([.. lineBytes]);
        }
        lineBytes.Add(buffer[i]);
        if (lineBytes.Count > LspProtocol.MaxFrameBytes) throw new IOException("frame too large");
      }
    }
  }

  private static async Task WriteLineAsync(NamedPipeServerStream server, string json, CancellationToken ct)
  {
    byte[] bytes = Encoding.UTF8.GetBytes(json + "\n");
    await server.WriteAsync(bytes, ct).ConfigureAwait(false);
    await server.FlushAsync(ct).ConfigureAwait(false);
  }

  public void Dispose()
  {
    foreach (NamedPipeServerStream connection in _connections.Keys)
    {
      connection.Dispose();
    }

    _connections.Clear();
    _servers?.Dispose();
    _servers = null;
    _shutdownCts.Dispose();
  }
}
