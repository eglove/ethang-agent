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
  private readonly TaskCompletionSource<bool> _loadCompleted = new(TaskCreationOptions.RunContinuationsAsynchronously);
  private bool _loadRequested;
  private bool _loaded;
  private DateTimeOffset _lastRequestAttempt;

  /// <summary>How long a failed load request waits before the next attempt. The send is a
  ///     single notification to a daemon that may still be starting, so a transient failure
  ///     is expected; a permanent one costs one retry per interval rather than a workspace
  ///     that never loads again.</summary>
  private static readonly TimeSpan RequestRetryInterval = TimeSpan.FromSeconds(2);

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
      _loadCompleted.TrySetResult(true);
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

  /// <summary>Requests the load and waits for the server's load-complete announcement,
  ///     bounded by <paramref name="timeout"/>. Returns the loaded state: false means the
  ///     load is still running and a query answered now has no compilation behind it.</summary>
  public async Task<bool> EnsureLoadedAsync(TimeSpan timeout, CancellationToken ct = default)
  {
    await RequestLoadAsync(ct).ConfigureAwait(false);
    if (IsLoaded)
    {
      return true;
    }

    using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
    deadline.CancelAfter(timeout);
    try
    {
      await _loadCompleted.Task.WaitAsync(deadline.Token).ConfigureAwait(false);
    }
    catch (OperationCanceledException)
    {
      return IsLoaded;
    }

    return IsLoaded;
  }

  /// <summary>Sends solution/open and returns without waiting. The load runs dotnet
  ///     restore and a build host, which takes far longer than an edit's advisory budget, so
  ///     a query during the load reports Starting and a later one finds the model ready.
  ///     Waiting here would delay the edit for a diagnostic it cannot show.
  ///     <para>THE REQUEST IS RETRIED, not attempted once: the flag is set only after the
  ///     send returns, so a send that throws leaves the model free to ask again on the next
  ///     query instead of reporting Starting for the life of the daemon.</para></summary>
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

      if (DateTimeOffset.UtcNow - _lastRequestAttempt < RequestRetryInterval)
      {
        return;
      }

      _lastRequestAttempt = DateTimeOffset.UtcNow;
    }

    string parameters = "{\"solution\":\"" + LspUriMapper.ToUri(solutionPath) + "\"}";
    await _transport.SendNotificationAsync("solution/open", parameters, ct).ConfigureAwait(false);
    lock (_gate) { _loadRequested = true; }
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
      _loadCompleted.TrySetResult(true);
      return true;
    }
    catch (System.Text.Json.JsonException)
    {
      return false;
    }
  }
}
