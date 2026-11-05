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

  private LspPipeClient(NamedPipeClientStream pipe) => _pipe = pipe;

  public string WorkspaceId { get; private set; } = string.Empty;
  public int ProtocolVersion { get; private set; }

  public static async Task<LspPipeClient> ConnectAsync(string pipeName, string workspaceId, string appInstanceId, CancellationToken ct = default)
  {
    NamedPipeClientStream pipe = new(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
    await pipe.ConnectAsync(ct).ConfigureAwait(false);
    LspPipeClient client = new(pipe);
    try
    {
      await client.HandshakeAsync(workspaceId, appInstanceId, ct).ConfigureAwait(false);
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
    string line = await ReadLineAsync(ct).ConfigureAwait(false);
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

  private async Task<string> ReadLineAsync(CancellationToken ct)
  {
    List<byte> lineBytes = [];
    byte[] buffer = new byte[8192];
    while (true)
    {
      int read = await _pipe.ReadAsync(buffer, ct).ConfigureAwait(false);
      if (read == 0) throw new IOException("daemon closed during hello");
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

  private int _nextRequestId = 2;

  private async Task<JsonObject> RequestAsync(string method, JsonObject parameters, CancellationToken ct)
  {
    int id = Interlocked.Increment(ref _nextRequestId);
    JsonObject request = new() { ["id"] = id, ["method"] = method, ["params"] = parameters };
    await WriteLineAsync(request.ToJsonString(), ct).ConfigureAwait(false);
    string line = await ReadLineAsync(ct).ConfigureAwait(false);
    using JsonDocument doc = JsonDocument.Parse(line);
    JsonElement root = doc.RootElement;
    if (root.TryGetProperty("error", out JsonElement err))
    {
      string code = err.TryGetProperty("code", out JsonElement c) ? c.GetString() ?? "unknown" : "unknown";
      throw new InvalidOperationException(method + " error: " + code);
    }
    return JsonNode.Parse(root.GetProperty("result").GetRawText())!.AsObject();
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

  /// <summary>Queries advisory diagnostics for one file from the daemon. The caller
  ///     states the watcher generation it last applied so the daemon can label a
  ///     result computed before a correction as Stale.</summary>
  public async Task<AdvisoryDiagnosticsResult> GetDiagnosticsAsync(string path, long knownGeneration = 0, CancellationToken ct = default)
  {
    ArgumentException.ThrowIfNullOrWhiteSpace(path);
    JsonObject result = await RequestAsync("lsp.diagnostics", new JsonObject { ["path"] = path, ["knownGeneration"] = knownGeneration }, ct).ConfigureAwait(false);
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
    return new AdvisoryDiagnosticsResult(state, diagnostics);
  }

  /// <summary>Issues one lsp.symbol query and returns the daemon's answer.</summary>
  public async Task<LspSymbolResult> SymbolAsync(string action, string uri, string symbolName, int occurrence, int depth, CancellationToken ct = default)
  {
    ArgumentException.ThrowIfNullOrWhiteSpace(action);
    JsonObject parameters = new()
    {
      ["action"] = action,
      ["uri"] = uri,
      ["symbolName"] = symbolName,
      ["occurrence"] = occurrence,
      ["depth"] = depth,
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
        result["message"]?.GetValue<string>());
  }

  /// <summary>Sends one watcher.announce notification over the pipe.</summary>
  public async Task SendWatcherAnnouncementAsync(WatcherAnnouncement announcement, CancellationToken ct = default)
  {
    ArgumentNullException.ThrowIfNull(announcement);
    await WriteLineAsync(WatcherAnnouncementCodec.Encode(announcement), ct).ConfigureAwait(false);
  }

  public void Dispose()
  {
    _writeLock.Dispose();
    _pipe.Dispose();
  }
}

public sealed class LspDaemonServer : IDisposable
{
  private readonly string _pipeName;
  private readonly string _workspaceId;
  private readonly Func<Task> _onConnected;
  private readonly string _daemonInstanceId = Guid.NewGuid().ToString("N");
  private LspDocumentSync? _forwarder;
  private LspDiagnosticsService? _diagnosticsService;
  private LspSymbolService? _symbolService;
  private readonly IFileClaimStore _claimStore;
  private readonly ConcurrentDictionary<NamedPipeServerStream, byte> _connections = new();
  private Func<CancellationToken, Task>? _restartHook;

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
      if (_forwarder is not null)
      {
        await _forwarder.ApplyAsync(announcement, ct).ConfigureAwait(false);
        _diagnosticsService?.LastAppliedGeneration = _forwarder.LastAppliedGeneration;
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
      if (method != "lsp.symbol" || _symbolService is null) return;
      if (!root.TryGetProperty("id", out JsonElement idEl)) return;
      if (!root.TryGetProperty("params", out JsonElement p)) return;
      string action = p.GetProperty("action").GetString() ?? string.Empty;
      string uri = p.TryGetProperty("uri", out JsonElement u) ? u.GetString() ?? string.Empty : string.Empty;
      string symbolName = p.TryGetProperty("symbolName", out JsonElement s) ? s.GetString() ?? string.Empty : string.Empty;
      int occurrence = p.TryGetProperty("occurrence", out JsonElement o) ? o.GetInt32() : 0;
      int depth = p.TryGetProperty("depth", out JsonElement d) ? d.GetInt32() : 1;
      LspSymbolResult result = action switch
      {
        "definition" => await _symbolService.DefinitionAsync(uri, symbolName, occurrence, ct).ConfigureAwait(false),
        "references" => await _symbolService.ReferencesAsync(uri, symbolName, occurrence, ct).ConfigureAwait(false),
        "hover" => await _symbolService.HoverAsync(uri, symbolName, occurrence, ct).ConfigureAwait(false),
        "implementation" => await _symbolService.ImplementationAsync(uri, symbolName, occurrence, ct).ConfigureAwait(false),
        "document-symbols" => await _symbolService.DocumentSymbolsAsync(uri, ct).ConfigureAwait(false),
        "workspace-symbols" => await _symbolService.WorkspaceSymbolsAsync(symbolName, ct).ConfigureAwait(false),
        "call-hierarchy-incoming" => await _symbolService.CallHierarchyIncomingAsync(uri, symbolName, occurrence, depth, ct).ConfigureAwait(false),
        "call-hierarchy-outgoing" => await _symbolService.CallHierarchyOutgoingAsync(uri, symbolName, occurrence, depth, ct).ConfigureAwait(false),
        _ => new LspSymbolResult(LspSymbolState.Unsupported, [], 0, 1, "unknown action " + action),
      };
      JsonObject reply = new() { ["id"] = idEl.GetInt32(), ["result"] = new JsonObject { ["state"] = result.State.ToString(), ["occurrenceUsed"] = result.OccurrenceUsed, ["totalOccurrences"] = result.TotalOccurrences, ["message"] = result.Message ?? string.Empty, ["sites"] = new JsonArray(result.Sites.Select(s => (JsonNode)new JsonObject { ["uri"] = s.Uri, ["line"] = s.Line, ["character"] = s.Character, ["symbolName"] = s.SymbolName }).ToArray()) } };
      await WriteLineAsync(server, reply.ToJsonString(), ct).ConfigureAwait(false);
    }
    catch (JsonException) { }
    catch (IOException) { }
  }

  private async Task HandleDiagnosticsRequestAsync(NamedPipeServerStream server, string line, CancellationToken ct)
  {
    try
    {
      using JsonDocument doc = JsonDocument.Parse(line);
      JsonElement root = doc.RootElement;
      if (!root.TryGetProperty("id", out JsonElement idEl)) return;
      if (!root.TryGetProperty("params", out JsonElement p)) return;
      string path = p.GetProperty("path").GetString() ?? string.Empty;
      long knownGeneration = p.TryGetProperty("knownGeneration", out JsonElement g) ? g.GetInt64() : 0;
      string uri = LspUriMapper.ToUri(path);
      LspDiagnosticsResult result = _diagnosticsService is null
        ? new LspDiagnosticsResult(LspDiagnosticsState.Unavailable, [])
        : await _diagnosticsService.PullAsync(uri, knownGeneration, ct).ConfigureAwait(false);
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
      JsonObject reply = new() { ["id"] = idEl.GetInt32(), ["result"] = new JsonObject { ["state"] = result.State.ToString(), ["diagnostics"] = diagnostics } };
      await WriteLineAsync(server, reply.ToJsonString(), ct).ConfigureAwait(false);
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
  }
}
