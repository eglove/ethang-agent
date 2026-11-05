namespace eThangAgent.ToolDomain.Lsp;

/// <summary>The symbol-navigation seam (issue #357): the Tool Domain's provider-neutral
/// view over language-server symbol queries. The ACL owns the wire; the domain never
/// learns transports exist. Expected failures flow as typed outcome values.</summary>
public interface ILspSymbolAccess
{
  Task<LspSymbolOutcome> ExecuteAsync(LspSymbolCommand command, CancellationToken ct = default);
}

/// <summary>Honesty label for a symbol query result, matching the diagnostics slice.</summary>
public enum LspSymbolState
{
  Ready,
  Starting,
  Stale,
  Degraded,
  Unavailable,
  Unsupported,
}

/// <summary>One location returned by a symbol query: file path plus 0-based line/character.</summary>
public sealed record LspSymbolSite(string Path, int Line, int Character, string SymbolName);

/// <summary>Commands the dispatch tool sends the seam (the closed command set).</summary>
// CA1034: the nested command/outcome cases ARE the seam's contract; public nested
// cases keep the case names as declared (the ComputerCommand named decision).
#pragma warning disable CA1034
public abstract record LspSymbolCommand
{
  public sealed record Definition(string Path, string SymbolName, int Occurrence) : LspSymbolCommand;
  public sealed record References(string Path, string SymbolName, int Occurrence) : LspSymbolCommand;
  public sealed record Hover(string Path, string SymbolName, int Occurrence) : LspSymbolCommand;
  public sealed record DocumentSymbols(string Path) : LspSymbolCommand;
  public sealed record WorkspaceSymbols(string SymbolName) : LspSymbolCommand;
  public sealed record Implementation(string Path, string SymbolName, int Occurrence) : LspSymbolCommand;
  public sealed record CallHierarchyIncoming(string Path, string SymbolName, int Occurrence, int Depth = 1) : LspSymbolCommand;
  public sealed record CallHierarchyOutgoing(string Path, string SymbolName, int Occurrence, int Depth = 1) : LspSymbolCommand;
}

/// <summary>Outcomes the seam returns (the closed outcome set).</summary>
public abstract record LspSymbolOutcome
{
  public sealed record Ready(IReadOnlyList<LspSymbolSite> Sites, int OccurrenceUsed = 0, int TotalOccurrences = 1, string? Message = null) : LspSymbolOutcome;
  public sealed record Unsupported(string Message) : LspSymbolOutcome;
  public sealed record Unavailable(string Message) : LspSymbolOutcome;
  public sealed record Starting(string Message) : LspSymbolOutcome;
  public sealed record Stale(string Message) : LspSymbolOutcome;
  public sealed record Degraded(string Message) : LspSymbolOutcome;
}
#pragma warning restore CA1034
