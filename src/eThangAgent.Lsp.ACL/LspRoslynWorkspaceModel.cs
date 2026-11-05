namespace eThangAgent.Lsp.ACL;

/// <summary>The Roslyn language server's workspace model. Roslyn answers an empty
/// diagnostic set for a workspace it has not loaded, so the load is not an optimization:
/// it is the precondition for any diagnostic at all. Sending <c>solution/open</c> starts
/// the load and the server announces <c>workspace/projectInitializationComplete</c> when
/// the compilation is ready. Document sync sent BEFORE that announcement is discarded -
/// the server has no project to attach the document to - so this model opens the document
/// only once the load has completed, and tracks its own version per document afterwards.</summary>
public sealed class LspRoslynWorkspaceModel : ILspWorkspaceModel
{
  private readonly LspJsonRpcTransport _transport;
  private readonly LspDocumentSync _documents;
  private readonly string? _solutionPath;
  private readonly Lock _gate = new();
  private bool _loadRequested;
  private bool _loaded;

  public LspRoslynWorkspaceModel(LspJsonRpcTransport transport, LspDocumentSync documents, string workspaceRoot)
  {
    _transport = transport ?? throw new ArgumentNullException(nameof(transport));
    _documents = documents ?? throw new ArgumentNullException(nameof(documents));
    ArgumentException.ThrowIfNullOrWhiteSpace(workspaceRoot);
    _solutionPath = LspWorkspaceSolution.Find(workspaceRoot);
    if (_solutionPath is null)
    {
      // Nothing to load: the server's file-based project system covers an opened document.
      _loaded = true;
    }
  }

  public bool IsLoaded
  {
    get { lock (_gate) { return _loaded; } }
  }

  public async Task PrepareAsync(string uri, CancellationToken ct = default)
  {
    ArgumentException.ThrowIfNullOrWhiteSpace(uri);
    await RequestLoadAsync(ct).ConfigureAwait(false);
    if (IsLoaded)
    {
      // The forwarder owns the open-document set, so this and a watcher announcement
      // cannot each send their own didOpen for the same document.
      await _documents.EnsureOpenAsync(uri, ct).ConfigureAwait(false);
    }
  }

  /// <summary>Sends solution/open once and returns without waiting. The load runs
  ///     dotnet restore and a build host, which takes far longer than an edit's advisory
  ///     budget, so a query during the load reports Starting and a later one finds the
  ///     model ready. Waiting here would delay the edit for a diagnostic it cannot show.</summary>
  private async Task RequestLoadAsync(CancellationToken ct)
  {
    string? solutionPath = _solutionPath;
    if (solutionPath is null)
    {
      return;
    }

    lock (_gate)
    {
      if (_loadRequested)
      {
        return;
      }

      _loadRequested = true;
    }

    string parameters = "{\"solution\":\"" + LspUriMapper.ToUri(solutionPath) + "\"}";
    await _transport.SendNotificationAsync("solution/open", parameters, ct).ConfigureAwait(false);
  }

  /// <summary>Records the server's load-complete announcement, which is the only signal
  /// that the compilation a diagnostic is computed from exists.</summary>
  public bool HandleNotification(string json)
  {
    ArgumentException.ThrowIfNullOrWhiteSpace(json);
    try
    {
      using System.Text.Json.JsonDocument doc = System.Text.Json.JsonDocument.Parse(json);
      System.Text.Json.JsonElement root = doc.RootElement;
      if (!root.TryGetProperty("method", out System.Text.Json.JsonElement method)
          || !string.Equals(method.GetString(), "workspace/projectInitializationComplete", StringComparison.Ordinal))
      {
        return false;
      }

      lock (_gate) { _loaded = true; }
      return true;
    }
    catch (System.Text.Json.JsonException)
    {
      return false;
    }
  }
}
