using System.Globalization;
using eThangAgent.SharedKernel;

namespace eThangAgent.ToolDomain.Lsp;

/// <summary>The 'lsp' dispatch tool (issue #357): the model's single door to
///     language-server symbol navigation. One tool on the loop registry keeps the
///     definition budget flat no matter how many operations exist - operations are
///     discovered through the action switch, never upfront definitions. Input is
///     parsed strictly by <see cref="LspSymbolToolInput"/>, dispatched to the
///     <see cref="ILspSymbolAccess"/> seam, and rendered on the fixed output
///     contract. The seam never throws domain errors - failures arrive as
///     <see cref="LspSymbolOutcome"/> values and are rendered, not caught.
///     Symbol-name addressing: the caller supplies path + symbolName (never
///     line/column); the ACL resolves the position via document symbols.</summary>
public sealed class LspSymbolTool(IPathResolver resolver, ILspSymbolAccess access, ILspRenameApplier? applier = null) : ITool, IWorkspaceScopedTool
{
  private readonly IPathResolver _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
  private readonly ILspSymbolAccess _access = access ?? throw new ArgumentNullException(nameof(access));
  private readonly ILspRenameApplier? _applier = applier;

  private static readonly string[] ActionNames = ["definition", "references", "hover", "document-symbols", "workspace-symbols", "implementation", "call-hierarchy-incoming", "call-hierarchy-outgoing", "workspace-load", "rename"];

  /// <summary>Actions that answer with a set of symbols rather than a resolved position, so
  ///     their annotation reports how many sites came back instead of an occurrence index.</summary>
  private static bool IsListingAction(LspSymbolAction action)
    => action is LspSymbolAction.DocumentSymbols or LspSymbolAction.WorkspaceSymbols or LspSymbolAction.WorkspaceLoad;

  public ITool RootedAt(string workspaceRoot)
      => new LspSymbolTool(new WorkspacePathResolver(workspaceRoot), _access, _applier);

  /// <inheritdoc />
  public ToolDefinition Definition { get; } = new(
      "lsp",
      BuildDescription(),
      [
          new ToolParameter(ToolTimeout.ParameterName, ToolParameterType.WholeNumber,
              ToolTimeout.ParameterDescription, Minimum: 1),
          new ToolParameter("action", ToolParameterType.Text,
              "Exactly one of definition, references, hover, document-symbols, workspace-symbols, implementation, call-hierarchy-incoming, call-hierarchy-outgoing, workspace-load, rename (case-sensitive)."),
          new ToolParameter("path", ToolParameterType.Text,
              "Required for definition, references, hover, implementation, document-symbols, rename: the file that defines the symbol."),
          new ToolParameter("symbolName", ToolParameterType.Text,
              "Required for definition, references, hover, implementation, workspace-symbols, rename: the symbol name. No line/column is accepted."),
          new ToolParameter("occurrence", ToolParameterType.WholeNumber,
              "Optional for position queries: 0-based occurrence index when the symbol occurs more than once (default 0)."),
          new ToolParameter("depth", ToolParameterType.WholeNumber,
              "Optional for call-hierarchy actions: 1 (default) or 2 levels."),
          new ToolParameter("newName", ToolParameterType.Text,
              "Required for rename: the name that replaces the symbol everywhere the server says it appears."),
          new ToolParameter("apply", ToolParameterType.Flag,
              "Optional for rename: false (default) reports the computed edits without writing; true writes every edit as one all-or-nothing unit."),
      ],
      [ToolTimeout.ParameterName, "action"]);

  private static string BuildDescription()
  {
    return "Query language-server symbol navigation through one dispatch surface."
        + " timeoutSeconds is mandatory. action is exactly one of " + string.Join(", ", ActionNames)
        + " (case-sensitive)."
        + " definition, references, hover, implementation, call-hierarchy-incoming, and call-hierarchy-outgoing require path and symbolName and take an optional occurrence (default 0); call-hierarchy actions take an optional depth (1 default, 2 max);"
        + " document-symbols requires path; workspace-symbols requires symbolName; workspace-load takes neither."
        + " rename requires path, symbolName, and newName, and takes an optional occurrence and an optional apply (default false)."
        + " rename asks the language server for the edit set and reports every edit as 'path:line:character -> newName' (1-based), marked not applied, unless apply is true, in which case every edit is written as one all-or-nothing unit and the result names the files changed."
        + " rename refuses paths outside the workspace and paths under bin/ or obj/, and reports Unsupported when the server does not advertise rename or the edit set carries file renames - file renames are not supported, only text edits."
        + " The caller never supplies line or column: the tool resolves the position from the symbol name via document symbols."
        + " Multiple occurrences resolve deterministically and the result names the occurrence used."
        + " A symbol that is only referenced (not defined) in the named file returns Unsupported, never a guessed position."
        + " A member resolves by its declared name even when the server reports it with its signature."
        + " hover carries the server's hover text after the site line."
        + " The first call to a workspace connects to its language server lazily; later calls reuse the session."
        + " A query against a workspace the server has not loaded returns Starting and never an empty Ready, because an unloaded server reports a symbol's own declaration and none of its cross-file uses."
        + " workspace-load requests the load and waits for it, so it is the action to call when a query reports Starting and you need a real answer."
        + " Output: one annotation line '[lsp] <State>: <action>' then one line per site - 'path:line:character symbol' (1-based)."
        + " States: Ready (sites returned), Starting (server still initializing - no evidence yet), Stale (results may be outdated),"
        + " Degraded (partial results), Unavailable (no language server for this workspace), Unsupported (capability not advertised or symbol only referenced)."
        + " An unsupported capability is never an empty list."
        + " Failures render 'Error [Code]: <message>'.";
  }

  /// <inheritdoc />
  public Task<ToolResult> ExecuteAsync(RawToolInput input, CancellationToken ct = default)
  {
    ArgumentNullException.ThrowIfNull(input);
    Result<LspSymbolToolInput> parsed = LspSymbolToolInput.Create(input.JsonArguments);
    if (!parsed.IsSuccess)
    {
      return Task.FromResult(Err(parsed.Error));
    }

    Result<ToolCallEnvelope> budget = ToolCallEnvelopeParser.Parse(input.Name, input.JsonArguments);
    return !budget.IsSuccess
      ? Task.FromResult(Err(budget.Error))
      : ToolExecution.RunAsync(input.Name, budget.Value.Timeout, token => RunAsync(parsed.Value, token), ct);
  }

  private async Task<ToolResult> RunAsync(LspSymbolToolInput args, CancellationToken ct)
  {
    string? resolvedPath = null;
    if (args.Path is not null)
    {
      Result<string> resolved = _resolver.Resolve(args.Path);
      if (!resolved.IsSuccess)
      {
        return Err(resolved.Error);
      }

      resolvedPath = resolved.Value;
    }

    LspSymbolCommand command = CommandOf(args, resolvedPath);
    LspSymbolOutcome outcome = await _access.ExecuteAsync(command, ct).ConfigureAwait(false);
    return outcome is LspSymbolOutcome.Renamed renamed && args.Apply
      ? await ApplyAsync(renamed, ct).ConfigureAwait(false)
      : Render(outcome, args);
  }

  /// <summary>Writes a computed rename as one unit and renders what changed. A rename that
  ///     arrives with no applier wired is a configuration failure, reported rather than
  ///     reported-as-not-applied, because the caller asked for the write.</summary>
  private async Task<ToolResult> ApplyAsync(LspSymbolOutcome.Renamed renamed, CancellationToken ct)
  {
    if (_applier is null)
    {
      return new ToolResult(
          "Error [Unavailable]: rename apply is not available in this session - no rename applier is wired, so 'apply: true' cannot be honoured. Re-run without 'apply' to see the computed edits.",
          true);
    }

    Result<LspRenameApplied> applied = await _applier.ApplyAsync(renamed.Edits, ct).ConfigureAwait(false);
    return applied.IsSuccess
      ? RenderApplied(renamed, applied.Value)
      : Err(applied.Error);
  }

  private static ToolResult RenderApplied(LspSymbolOutcome.Renamed renamed, LspRenameApplied applied)
  {
    List<string> lines =
    [
      string.Create(CultureInfo.InvariantCulture,
          $"[lsp] Ready: rename - applied {applied.EditCount} {EditPlural(applied.EditCount)} to {applied.FilesChanged.Count} {FilePlural(applied.FilesChanged.Count)} for '{renamed.Target}'"),
    ];
    foreach (string file in applied.FilesChanged)
    {
      lines.Add(file);
    }

    if (!string.IsNullOrWhiteSpace(renamed.Message))
    {
      lines.Add(renamed.Message);
    }

    return new ToolResult(string.Join("\n", lines), false);
  }

  /// <summary>One row per action: how the action is named on the wire and the command it
  ///     builds. The name and the command live together so neither can be added without the
  ///     other, and no switch over the enum has to grow with the action set.</summary>
  private sealed record ActionRow(LspSymbolAction Action, string Name, Func<LspSymbolToolInput, string?, LspSymbolCommand> Command);

  private static readonly ActionRow[] Rows =
  [
    new(LspSymbolAction.Definition, "definition", (a, p) => new LspSymbolCommand.Definition(p!, a.SymbolName!, a.Occurrence)),
    new(LspSymbolAction.References, "references", (a, p) => new LspSymbolCommand.References(p!, a.SymbolName!, a.Occurrence)),
    new(LspSymbolAction.Hover, "hover", (a, p) => new LspSymbolCommand.Hover(p!, a.SymbolName!, a.Occurrence)),
    new(LspSymbolAction.DocumentSymbols, "document-symbols", (a, p) => new LspSymbolCommand.DocumentSymbols(p!)),
    new(LspSymbolAction.WorkspaceSymbols, "workspace-symbols", (a, _) => new LspSymbolCommand.WorkspaceSymbols(a.SymbolName!)),
    new(LspSymbolAction.Implementation, "implementation", (a, p) => new LspSymbolCommand.Implementation(p!, a.SymbolName!, a.Occurrence)),
    new(LspSymbolAction.CallHierarchyIncoming, "call-hierarchy-incoming", (a, p) => new LspSymbolCommand.CallHierarchyIncoming(p!, a.SymbolName!, a.Occurrence, a.Depth)),
    new(LspSymbolAction.CallHierarchyOutgoing, "call-hierarchy-outgoing", (a, p) => new LspSymbolCommand.CallHierarchyOutgoing(p!, a.SymbolName!, a.Occurrence, a.Depth)),
    new(LspSymbolAction.WorkspaceLoad, "workspace-load", (_, _) => new LspSymbolCommand.WorkspaceLoad()),
    new(LspSymbolAction.Rename, "rename", (a, p) => new LspSymbolCommand.Rename(p!, a.SymbolName!, a.Occurrence, a.NewName!)),
  ];

  private static ActionRow RowOf(LspSymbolAction action)
    => Rows.FirstOrDefault(row => row.Action == action)
        ?? throw new InvalidOperationException("unreachable: action is a validated enum");

  private static LspSymbolCommand CommandOf(LspSymbolToolInput args, string? resolvedPath)
    => RowOf(args.Action).Command(args, resolvedPath);

  private static string ActionText(LspSymbolAction action) => RowOf(action).Name;

  private static string Pluralize(int count) => count == 1 ? "symbol" : "symbols";

  private static ToolResult Render(LspSymbolOutcome outcome, LspSymbolToolInput args)
  {
    string actionText = ActionText(args.Action);
    return outcome switch
    {
      LspSymbolOutcome.Ready ready => RenderReady(ready, actionText, args),
      LspSymbolOutcome.Renamed renamed => RenderRenamed(renamed, actionText),
      LspSymbolOutcome.Unsupported u => new ToolResult($"[lsp] Unsupported: {actionText} - {u.Message}", false),
      LspSymbolOutcome.Unavailable u => new ToolResult($"[lsp] Unavailable: {actionText} - {u.Message}", false),
      LspSymbolOutcome.Starting s => new ToolResult($"[lsp] Starting: {actionText} - {s.Message}", false),
      LspSymbolOutcome.Stale s => new ToolResult($"[lsp] Stale: {actionText} - {s.Message}", false),
      LspSymbolOutcome.Degraded d => new ToolResult($"[lsp] Degraded: {actionText} - {d.Message}", false),
      _ => throw new InvalidOperationException("unreachable: outcome is a validated record family"),
    };
  }

  private static ToolResult RenderReady(LspSymbolOutcome.Ready ready, string actionText, LspSymbolToolInput args)
  {
    List<string> lines = [];
    lines.Add(IsListingAction(args.Action)
        ? string.Create(CultureInfo.InvariantCulture, $"[lsp] Ready: {actionText} - {ready.Sites.Count} {Pluralize(ready.Sites.Count)}")
        : string.Create(CultureInfo.InvariantCulture,
            $"[lsp] Ready: {actionText} - occurrence {ready.OccurrenceUsed} of {ready.TotalOccurrences} for '{args.SymbolName ?? args.Path}'"));
    foreach (LspSymbolSite site in ready.Sites)
    {
      lines.Add(string.Create(CultureInfo.InvariantCulture,
          $"{site.Path}:{site.Line + 1}:{site.Character + 1} {site.SymbolName}"));
    }

    if (!string.IsNullOrWhiteSpace(ready.Message))
    {
      lines.Add(ready.Message);
    }

    return new ToolResult(string.Join("\n", lines), false);
  }

  /// <summary>Renders a computed rename that was not written: every edit as
  ///     'path:line:character -> newName' (1-based), then the words 'not applied'.</summary>
  private static ToolResult RenderRenamed(LspSymbolOutcome.Renamed renamed, string actionText)
  {
    int count = renamed.Edits.Count;
    List<string> lines =
    [
      string.Create(CultureInfo.InvariantCulture,
          $"[lsp] Ready: {actionText} - {count} {EditPlural(count)} for '{renamed.Target}' (not applied)"),
    ];
    foreach (LspRenameEdit edit in renamed.Edits)
    {
      lines.Add(string.Create(CultureInfo.InvariantCulture,
          $"{edit.Path}:{edit.Line + 1}:{edit.Character + 1} -> {edit.NewText}"));
    }

    if (!string.IsNullOrWhiteSpace(renamed.Message))
    {
      lines.Add(renamed.Message);
    }

    return new ToolResult(string.Join("\n", lines), false);
  }

  private static string EditPlural(int count) => count == 1 ? "edit" : "edits";

  private static string FilePlural(int count) => count == 1 ? "file" : "files";

  private static ToolResult Err(DomainError error) => new(
      $"Error [{error.Code}]: {error.Message}", true);
}
