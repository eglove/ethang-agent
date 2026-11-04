namespace eThangAgent.Lsp.ACL;

/// <summary>Document-sync sink the forwarder drives (issue #362). The production
/// implementation serializes didOpen/didChange/didClose notifications to the
/// language server over the JSON-RPC transport; tests record calls.</summary>
public interface ILspDocumentSync
{
  Task DidOpenAsync(string uri, string languageId, int version, string text, CancellationToken ct = default);
  Task DidChangeAsync(string uri, int version, string text, CancellationToken ct = default);
  Task DidCloseAsync(string uri, CancellationToken ct = default);
}

/// <summary>Forwards watcher announcements to the language server (issue #362).
/// Reads current disk text at forward time: didOpen for a new path, didChange for
/// a tracked path, didClose for deletedPaths and for any read failure. Records
/// LastAppliedGeneration and ignores generation &lt;= last applied, so data
/// computed before a correction can be labeled stale via IsStale.</summary>
public sealed class LspDocumentSync
{
  private readonly string _workspaceId;
  private readonly string _workspaceRoot;
  private readonly string _languageId;
  private readonly HashSet<string> _extensions;
  private readonly ILspDocumentSync _sink;
  private readonly Dictionary<string, int> _openVersions = new(StringComparer.Ordinal);
  private readonly Lock _gate = new();

  public LspDocumentSync(string workspaceId, string workspaceRoot, string languageId, IReadOnlyList<string> extensions, ILspDocumentSync sink)
  {
    ArgumentException.ThrowIfNullOrWhiteSpace(workspaceId);
    ArgumentException.ThrowIfNullOrWhiteSpace(workspaceRoot);
    ArgumentException.ThrowIfNullOrWhiteSpace(languageId);
    _workspaceId = workspaceId;
    _workspaceRoot = Path.GetFullPath(workspaceRoot);
    _languageId = languageId;
    _extensions = new HashSet<string>(extensions.Select(NormalizeExtension), StringComparer.OrdinalIgnoreCase);
    _sink = sink ?? throw new ArgumentNullException(nameof(sink));
  }

  public long LastAppliedGeneration { get; private set; }

  public IReadOnlyList<string> OpenDocuments
  {
    get { lock (_gate) { return [.. _openVersions.Keys]; } }
  }

  public bool IsStale(long knownGeneration) => knownGeneration < LastAppliedGeneration;

  public async Task ApplyAsync(WatcherAnnouncement announcement, CancellationToken ct = default)
  {
    ArgumentNullException.ThrowIfNull(announcement);
    if (!string.Equals(announcement.WorkspaceId, _workspaceId, StringComparison.Ordinal)) return;
    lock (_gate)
    {
      if (announcement.Generation <= LastAppliedGeneration) return;
    }
    foreach (string relative in announcement.Paths)
    {
      if (!Claims(relative)) continue;
      string fullPath = Path.Combine(_workspaceRoot, relative.Replace('/', Path.DirectorySeparatorChar));
      string uri = LspUriMapper.ToUri(fullPath);
      string? text = await TryReadTextAsync(fullPath, ct).ConfigureAwait(false);
      bool isOpen;
      lock (_gate) { isOpen = _openVersions.ContainsKey(relative); }
      if (text is null)
      {
        await _sink.DidCloseAsync(uri, ct).ConfigureAwait(false);
        lock (_gate) { _ = _openVersions.Remove(relative); }
      }
      else if (isOpen)
      {
        int version = checked((int)announcement.Generation);
        await _sink.DidChangeAsync(uri, version, text, ct).ConfigureAwait(false);
        lock (_gate) { _openVersions[relative] = version; }
      }
      else
      {
        int version = checked((int)announcement.Generation);
        await _sink.DidOpenAsync(uri, _languageId, version, text, ct).ConfigureAwait(false);
        lock (_gate) { _openVersions[relative] = version; }
      }
    }
    foreach (string relative in announcement.DeletedPaths)
    {
      if (!Claims(relative)) continue;
      string fullPath = Path.Combine(_workspaceRoot, relative.Replace('/', Path.DirectorySeparatorChar));
      string uri = LspUriMapper.ToUri(fullPath);
      await _sink.DidCloseAsync(uri, ct).ConfigureAwait(false);
      lock (_gate) { _ = _openVersions.Remove(relative); }
    }
    lock (_gate) { LastAppliedGeneration = announcement.Generation; }
  }

  /// <summary>Re-opens every previously open document from current disk text.
  /// Called after a daemon restart before serving queries, so no query is
  /// answered against a stale or empty server model.</summary>
  public async Task ReopenAllAsync(IReadOnlyList<string> openDocuments, CancellationToken ct = default)
  {
    ArgumentNullException.ThrowIfNull(openDocuments);
    foreach (string relative in openDocuments)
    {
      if (!Claims(relative)) continue;
      string fullPath = Path.Combine(_workspaceRoot, relative.Replace('/', Path.DirectorySeparatorChar));
      string uri = LspUriMapper.ToUri(fullPath);
      string? text = await TryReadTextAsync(fullPath, ct).ConfigureAwait(false);
      if (text is null)
      {
        await _sink.DidCloseAsync(uri, ct).ConfigureAwait(false);
        continue;
      }
      int version;
      lock (_gate)
      {
        version = _openVersions.TryGetValue(relative, out int v) ? v : 1;
        _openVersions[relative] = version;
      }
      await _sink.DidOpenAsync(uri, _languageId, version, text, ct).ConfigureAwait(false);
    }
  }

  private bool Claims(string relative)
  {
    string ext = Path.GetExtension(relative);
    return !string.IsNullOrWhiteSpace(ext) && _extensions.Contains(NormalizeExtension(ext));
  }

  private static string NormalizeExtension(string extension)
  {
    string ext = extension.StartsWith('.') ? extension : "." + extension;
    return ext.ToUpperInvariant();
  }

  private static async Task<string?> TryReadTextAsync(string fullPath, CancellationToken ct)
  {
    try
    {
      return await File.ReadAllTextAsync(fullPath, ct).ConfigureAwait(false);
    }
    catch (IOException) { return null; }
    catch (UnauthorizedAccessException) { return null; }
  }
}
