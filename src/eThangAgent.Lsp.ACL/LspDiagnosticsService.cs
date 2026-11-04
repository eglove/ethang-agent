using System.Text.Json;

namespace eThangAgent.Lsp.ACL;

/// <summary>Production <see cref="ILspDocumentSync"/> over the JSON-RPC transport
/// (issue #354): didOpen/didChange/didClose notifications to the language server.</summary>
public sealed class LspTransportDocumentSync : ILspDocumentSync
{
  private readonly LspJsonRpcTransport _transport;

  public LspTransportDocumentSync(LspJsonRpcTransport transport)
    => _transport = transport ?? throw new ArgumentNullException(nameof(transport));

  public async Task DidOpenAsync(string uri, string languageId, int version, string text, CancellationToken ct = default)
  {
    ArgumentException.ThrowIfNullOrWhiteSpace(uri);
    ArgumentException.ThrowIfNullOrWhiteSpace(languageId);
    ArgumentNullException.ThrowIfNull(text);
    string p = "{\"textDocument\":{\"uri\":\"" + uri + "\",\"languageId\":\"" + languageId + "\",\"version\":" + version + ",\"text\":" + JsonSerializer.Serialize(text) + "}}";
    await _transport.SendNotificationAsync("textDocument/didOpen", p, ct).ConfigureAwait(false);
  }

  public async Task DidChangeAsync(string uri, int version, string text, CancellationToken ct = default)
  {
    ArgumentException.ThrowIfNullOrWhiteSpace(uri);
    ArgumentNullException.ThrowIfNull(text);
    string p = "{\"textDocument\":{\"uri\":\"" + uri + "\",\"version\":" + version + "},\"contentChanges\":[{\"text\":" + JsonSerializer.Serialize(text) + "}]}";
    await _transport.SendNotificationAsync("textDocument/didChange", p, ct).ConfigureAwait(false);
  }

  public async Task DidCloseAsync(string uri, CancellationToken ct = default)
  {
    ArgumentException.ThrowIfNullOrWhiteSpace(uri);
    string p = "{\"textDocument\":{\"uri\":\"" + uri + "\"}}";
    await _transport.SendNotificationAsync("textDocument/didClose", p, ct).ConfigureAwait(false);
  }
}

/// <summary>Routes server-to-client notifications into the diagnostics store
/// (issue #354): textDocument/publishDiagnostics is the push path.</summary>
public sealed class LspNotificationRouter
{
  private readonly LspDiagnosticStore _store;

  public LspNotificationRouter(LspDiagnosticStore store)
    => _store = store ?? throw new ArgumentNullException(nameof(store));

  public bool HandleNotification(string json)
  {
    ArgumentException.ThrowIfNullOrWhiteSpace(json);
    try
    {
      using JsonDocument doc = JsonDocument.Parse(json);
      JsonElement root = doc.RootElement;
      if (!root.TryGetProperty("method", out JsonElement m) || m.GetString() != "textDocument/publishDiagnostics") return false;
      if (!root.TryGetProperty("params", out JsonElement p)) return false;
      _store.OnPublishDiagnostics(p.GetRawText());
      return true;
    }
    catch (JsonException) { return false; }
  }
}

/// <summary>Production diagnostics query service (issue #354): executes
/// textDocument/diagnostic pull requests, merges with push via the store, and
/// enforces the restart query gate. Null transport means no server (Unavailable).</summary>
public sealed class LspDiagnosticsService
{
  private readonly LspJsonRpcTransport? _transport;
  private readonly LspDiagnosticStore _store;
  private readonly LspCapabilities _capabilities;
  private readonly SemaphoreSlim _restartGate = new(1, 1);

  public LspDiagnosticsService(LspJsonRpcTransport? transport, LspDiagnosticStore store)
    : this(transport, store, LspCapabilities.None)
  {
  }

  public LspDiagnosticsService(LspJsonRpcTransport? transport, LspDiagnosticStore store, LspCapabilities capabilities)
    => (_transport, _store, _capabilities) = (transport, store ?? throw new ArgumentNullException(nameof(store)), capabilities ?? throw new ArgumentNullException(nameof(capabilities)));

  public void BeginRestart() => _ = _restartGate.WaitAsync();

  public async Task CompleteRestartAsync()
  {
    _ = _restartGate.Release();
    await Task.CompletedTask.ConfigureAwait(false);
  }

  public async Task<LspDiagnosticsResult> PullAsync(string uri, CancellationToken ct = default)
  {
    ArgumentException.ThrowIfNullOrWhiteSpace(uri);
    await _restartGate.WaitAsync(ct).ConfigureAwait(false);
    try
    {
      if (_transport is null)
      {
        LspDiagnosticsResult existing = _store.Get(uri);
        return existing.State == LspDiagnosticsState.Starting
          ? new LspDiagnosticsResult(LspDiagnosticsState.Unavailable, [])
          : existing;
      }
      if (!_capabilities.Supports("textDocument/diagnostic"))
      {
        return _store.Get(uri);
      }
      string pullParams = _store.BuildPullParams(uri);
      LspRequestResult response = await _transport.SendRequestAsync("textDocument/diagnostic", pullParams, ct).ConfigureAwait(false);
      if (!response.IsSuccess)
      {
        LspDiagnosticsResult existing = _store.Get(uri);
        if (existing.Diagnostics.Count > 0)
        {
          return new LspDiagnosticsResult(LspDiagnosticsState.Degraded, existing.Diagnostics);
        }
        return new LspDiagnosticsResult(LspDiagnosticsState.Degraded, []);
      }
      _store.OnPullDiagnostics(uri, response.ResultJson ?? "{}");
      return _store.Get(uri);
    }
    finally
    {
      _ = _restartGate.Release();
    }
  }
}
