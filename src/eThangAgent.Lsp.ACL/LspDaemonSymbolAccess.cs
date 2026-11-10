using System.Text.Json;
using eThangAgent.ToolDomain.Lsp;

namespace eThangAgent.Lsp.ACL;

/// <summary>Production <see cref="ILspSymbolAccess"/> over the daemon pipe: translates
/// domain commands into the daemon's lsp.symbol request and maps the honesty labels
/// back. The client is resolved through an async factory, so a cold daemon never
/// blocks the caller and an unreachable daemon yields Unavailable.</summary>
public sealed class LspDaemonSymbolAccess : ILspSymbolAccess
{
  private const string RenameAction = "rename";

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
    (string action, string? path, string? symbolName, int occurrence, int depth, string newName) = Describe(command);
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
        .SymbolAsync(action, uri, symbolName ?? string.Empty, occurrence, depth, newName, ct)
        .ConfigureAwait(false);
    return Map(result, action, symbolName ?? string.Empty, newName);
  }

  private static (string Action, string? Path, string? SymbolName, int Occurrence, int Depth, string NewName) Describe(LspSymbolCommand command) => command switch
  {
    LspSymbolCommand.Definition c => ("definition", c.Path, c.SymbolName, c.Occurrence, 1, string.Empty),
    LspSymbolCommand.References c => ("references", c.Path, c.SymbolName, c.Occurrence, 1, string.Empty),
    LspSymbolCommand.Hover c => ("hover", c.Path, c.SymbolName, c.Occurrence, 1, string.Empty),
    LspSymbolCommand.DocumentSymbols c => ("document-symbols", c.Path, null, 0, 1, string.Empty),
    LspSymbolCommand.WorkspaceSymbols c => ("workspace-symbols", null, c.SymbolName, 0, 1, string.Empty),
    LspSymbolCommand.Implementation c => ("implementation", c.Path, c.SymbolName, c.Occurrence, 1, string.Empty),
    LspSymbolCommand.CallHierarchyIncoming c => ("call-hierarchy-incoming", c.Path, c.SymbolName, c.Occurrence, c.Depth, string.Empty),
    LspSymbolCommand.CallHierarchyOutgoing c => ("call-hierarchy-outgoing", c.Path, c.SymbolName, c.Occurrence, c.Depth, string.Empty),
    LspSymbolCommand.WorkspaceLoad => ("workspace-load", null, null, 0, 1, string.Empty),
    LspSymbolCommand.Rename c => (RenameAction, c.Path, c.SymbolName, c.Occurrence, 1, c.NewName),
    _ => throw new InvalidOperationException("unreachable: command is a validated record family"),
  };

  private static LspSymbolOutcome Map(LspSymbolResult result, string action, string symbolName, string newName)
  {
    string message = result.Message ?? result.State.ToString();
    List<ToolDomain.Lsp.LspSymbolSite> sites = [.. result.Sites.Select(s => new ToolDomain.Lsp.LspSymbolSite(FromUri(s.Uri), s.Line, s.Character, s.SymbolName))];
    return result.State switch
    {
      LspSymbolState.Ready when action == RenameAction => MapRename(result, symbolName, newName),
      LspSymbolState.Ready => new LspSymbolOutcome.Ready(sites, result.OccurrenceUsed, result.TotalOccurrences, result.Message, result.LanguageId),
      LspSymbolState.Unsupported => new LspSymbolOutcome.Unsupported(message, result.LanguageId),
      LspSymbolState.Unavailable => new LspSymbolOutcome.Unavailable(message, result.LanguageId),
      LspSymbolState.Starting => new LspSymbolOutcome.Starting(message, result.LanguageId),
      LspSymbolState.Stale => new LspSymbolOutcome.Stale(message, result.LanguageId),
      LspSymbolState.Degraded => new LspSymbolOutcome.Degraded(message, result.LanguageId),
      _ => new LspSymbolOutcome.Degraded(message),
    };
  }

  /// <summary>Flattens a rename WorkspaceEdit into the domain's edit values. The wire shape
  ///     never crosses the seam: a file move is unsupported, and a payload that does not
  ///     parse is a degraded answer rather than a guess.</summary>
  private static LspSymbolOutcome MapRename(LspSymbolResult result, string target, string newName)
  {
    if (string.IsNullOrWhiteSpace(result.Message))
    {
      return new LspSymbolOutcome.Unsupported("the language server returned no rename edit set");
    }

    LspRenameEditSet editSet;
    try
    {
      editSet = LspRenameEdits.Parse(result.Message);
    }
    catch (JsonException ex)
    {
      return new LspSymbolOutcome.Degraded("the rename edit set could not be read: " + ex.Message);
    }

    if (editSet.HasFileRenames)
    {
      return new LspSymbolOutcome.Unsupported(
          "this rename also moves files, which is not supported; only text edits can be applied");
    }

    List<LspRenameEdit> edits = [.. editSet.Documents.SelectMany(document =>
        document.Edits.Select(edit => new LspRenameEdit(
            FromUri(document.Uri), edit.StartLine, edit.StartCharacter, edit.EndLine, edit.EndCharacter, edit.NewText)))];
    return new LspSymbolOutcome.Renamed(edits, target, newName, Applied: false, result.Message, result.LanguageId);
  }

  private static string FromUri(string uri)
  {
    try { return LspUriMapper.FromUri(uri); }
    catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or UriFormatException) { return uri; }
  }
}
