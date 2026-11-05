using eThangAgent.ToolDomain.Lsp;

namespace eThangAgent.Lsp.ACL;

/// <summary>Production <see cref="ILspSymbolAccess"/> over the daemon pipe: translates
/// domain commands into the daemon's lsp.symbol request and maps the honesty labels
/// back. The client is resolved through an async factory, so a cold daemon never
/// blocks the caller and an unreachable daemon yields Unavailable.</summary>
public sealed class LspDaemonSymbolAccess : ILspSymbolAccess
{
  private readonly Func<CancellationToken, Task<LspPipeClient?>> _clientFactory;
  private readonly Func<string, bool> _fileExists;

  public LspDaemonSymbolAccess(Func<CancellationToken, Task<LspPipeClient?>> clientFactory, Func<string, bool>? fileExists = null)
  {
    _clientFactory = clientFactory ?? throw new ArgumentNullException(nameof(clientFactory));
    _fileExists = fileExists ?? File.Exists;
  }

  public async Task<LspSymbolOutcome> ExecuteAsync(LspSymbolCommand command, CancellationToken ct = default)
  {
    ArgumentNullException.ThrowIfNull(command);
    (string action, string? path, string? symbolName, int occurrence, int depth) = Describe(command);
    if (path is not null && !_fileExists(path))
    {
      return new LspSymbolOutcome.Unsupported("the file '" + path + "' does not exist");
    }

    LspPipeClient? client = await _clientFactory(ct).ConfigureAwait(false);
    if (client is null)
    {
      return new LspSymbolOutcome.Unavailable("no language server for this workspace");
    }

    string uri = path is null ? string.Empty : LspUriMapper.ToUri(path);
    LspSymbolResult result = await client
        .SymbolAsync(action, uri, symbolName ?? string.Empty, occurrence, depth, ct)
        .ConfigureAwait(false);
    return Map(result);
  }

  private static (string Action, string? Path, string? SymbolName, int Occurrence, int Depth) Describe(LspSymbolCommand command) => command switch
  {
    LspSymbolCommand.Definition c => ("definition", c.Path, c.SymbolName, c.Occurrence, 1),
    LspSymbolCommand.References c => ("references", c.Path, c.SymbolName, c.Occurrence, 1),
    LspSymbolCommand.Hover c => ("hover", c.Path, c.SymbolName, c.Occurrence, 1),
    LspSymbolCommand.DocumentSymbols c => ("document-symbols", c.Path, null, 0, 1),
    LspSymbolCommand.WorkspaceSymbols c => ("workspace-symbols", null, c.SymbolName, 0, 1),
    LspSymbolCommand.Implementation c => ("implementation", c.Path, c.SymbolName, c.Occurrence, 1),
    LspSymbolCommand.CallHierarchyIncoming c => ("call-hierarchy-incoming", c.Path, c.SymbolName, c.Occurrence, c.Depth),
    LspSymbolCommand.CallHierarchyOutgoing c => ("call-hierarchy-outgoing", c.Path, c.SymbolName, c.Occurrence, c.Depth),
    LspSymbolCommand.WorkspaceLoad => ("workspace-load", null, null, 0, 1),
    _ => throw new InvalidOperationException("unreachable: command is a validated record family"),
  };

  private static LspSymbolOutcome Map(LspSymbolResult result)
  {
    string message = result.Message ?? result.State.ToString();
    List<ToolDomain.Lsp.LspSymbolSite> sites = [.. result.Sites.Select(s => new ToolDomain.Lsp.LspSymbolSite(FromUri(s.Uri), s.Line, s.Character, s.SymbolName))];
    return result.State switch
    {
      LspSymbolState.Ready => new LspSymbolOutcome.Ready(sites, result.OccurrenceUsed, result.TotalOccurrences, result.Message),
      LspSymbolState.Unsupported => new LspSymbolOutcome.Unsupported(message),
      LspSymbolState.Unavailable => new LspSymbolOutcome.Unavailable(message),
      LspSymbolState.Starting => new LspSymbolOutcome.Starting(message),
      LspSymbolState.Stale => new LspSymbolOutcome.Stale(message),
      LspSymbolState.Degraded => new LspSymbolOutcome.Degraded(message),
      _ => new LspSymbolOutcome.Degraded(message),
    };
  }

  private static string FromUri(string uri)
  {
    try { return LspUriMapper.FromUri(uri); }
    catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or UriFormatException) { return uri; }
  }
}
