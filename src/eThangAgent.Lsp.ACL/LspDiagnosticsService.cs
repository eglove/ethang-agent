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
/// enforces the restart query gate. Null transport means no server (Unavailable).
/// A workspace model is consulted first: a server that has not loaded the workspace
/// reports no diagnostics for any file, and reporting that as Ready would read as a
/// clean file.
///     <para>A REPORT IS EVIDENCE ONLY WHEN A COMPILATION STANDS BEHIND IT. Without one
///     the server answers from a file-based fallback that parses the document and resolves
///     nothing, so an empty set says nothing about whether the file compiles. Such a report
///     is labeled rather than returned as Ready, and a non-empty one is passed through
///     because a syntax error needs no compilation to be real.</para></summary>
public sealed class LspDiagnosticsService
{
  private readonly LspJsonRpcTransport? _transport;
  private readonly LspDiagnosticStore _store;
  private readonly LspCapabilities _capabilities;
  private readonly ILspWorkspaceModel? _workspace;
  private readonly SemaphoreSlim _restartGate = new(1, 1);

  public LspDiagnosticsService(LspJsonRpcTransport? transport, LspDiagnosticStore store)
    : this(transport, store, LspCapabilities.None)
  {
  }

  public LspDiagnosticsService(LspJsonRpcTransport? transport, LspDiagnosticStore store, LspCapabilities capabilities)
    : this(transport, store, capabilities, workspace: null)
  {
  }

  public LspDiagnosticsService(
      LspJsonRpcTransport? transport,
      LspDiagnosticStore store,
      LspCapabilities capabilities,
      ILspWorkspaceModel? workspace)
    => (_transport, _store, _capabilities, _workspace) = (transport, store ?? throw new ArgumentNullException(nameof(store)), capabilities ?? throw new ArgumentNullException(nameof(capabilities)), workspace);

  /// <summary>The request id the server uses for "content modified": a pull issued while
  /// the server is still applying a document change is refused with it. The answer would
  /// be stale by construction, so it is reported as Starting - the server has not reported
  /// for this content yet - and the next query asks again.</summary>
  private const string ContentModifiedCode = "-32800";

  /// <summary>How long one pull may occupy the daemon. A pull that outlives this is
  /// abandoned and reported Starting: the answer would arrive after the edit that asked
  /// for it, and a query that never returns would hold the pipe against every later one.</summary>
  private static readonly TimeSpan PullDeadline = TimeSpan.FromSeconds(10);

  /// <summary>Pull is available when the server advertises it in its initialize result
  /// or registers it afterwards. Roslyn registers textDocument/diagnostic through
  /// client/registerCapability and advertises no diagnosticProvider at all, so the
  /// static record alone would leave pull unreachable.</summary>
  private bool SupportsPull()
    => _capabilities.Supports("textDocument/diagnostic")
        || (_transport is not null && _transport.DynamicRegistrations.Contains("textDocument/diagnostic"));

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
      if (_workspace is not null)
      {
        await _workspace.PrepareAsync(uri, ct).ConfigureAwait(false);
        if (!_workspace.IsLoaded)
        {
          return new LspDiagnosticsResult(LspDiagnosticsState.Starting, []);
        }
      }
      if (!SupportsPull())
      {
        return _store.Get(uri);
      }
      LspRequestResult response;
      using (CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(ct))
      {
        deadline.CancelAfter(PullDeadline);
        try
        {
          response = await _transport.SendRequestAsync(
              "textDocument/diagnostic", LspDiagnosticStore.BuildPullParams(uri), deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
          return new LspDiagnosticsResult(LspDiagnosticsState.Starting, []);
        }
      }

      if (!response.IsSuccess && string.Equals(response.ErrorCode, ContentModifiedCode, StringComparison.Ordinal))
      {
        return new LspDiagnosticsResult(LspDiagnosticsState.Starting, []);
      }

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
      LspDiagnosticsResult reported = _store.Get(uri);
      // A report from a server with no compilation behind it answers a different question:
      // it resolves no names, so an empty set says nothing about whether the file compiles.
      // Reporting it as Ready would read as clean, which is the one thing an empty set must
      // never mean. Syntax diagnostics are still real evidence and are passed through.
      if (_workspace is not null && !_workspace.HasCompilation && reported.State == LspDiagnosticsState.Ready)
      {
        return reported.Diagnostics.Count > 0
          ? new LspDiagnosticsResult(LspDiagnosticsState.Degraded, reported.Diagnostics)
          : new LspDiagnosticsResult(LspDiagnosticsState.Starting, []);
      }
      return reported;
    }
    finally
    {
      _ = _restartGate.Release();
    }
  }
}
