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
public sealed class LspSymbolTool(IPathResolver resolver, ILspSymbolAccess access) : ITool, IWorkspaceScopedTool
{
  private readonly IPathResolver _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
  private readonly ILspSymbolAccess _access = access ?? throw new ArgumentNullException(nameof(access));

  private static readonly string[] ActionNames = ["definition", "references", "hover", "document-symbols", "workspace-symbols", "implementation", "call-hierarchy-incoming", "call-hierarchy-outgoing"];

  public ITool RootedAt(string workspaceRoot)
      => new LspSymbolTool(new WorkspacePathResolver(workspaceRoot), _access);

  /// <inheritdoc />
  public ToolDefinition Definition { get; } = new(
      "lsp",
      BuildDescription(),
      [
          new ToolParameter(ToolTimeout.ParameterName, ToolParameterType.WholeNumber,
              ToolTimeout.ParameterDescription, Minimum: 1),
          new ToolParameter("action", ToolParameterType.Text,
              "Exactly one of definition, references, hover, document-symbols, workspace-symbols, implementation, call-hierarchy-incoming, call-hierarchy-outgoing (case-sensitive)."),
          new ToolParameter("path", ToolParameterType.Text,
              "Required for definition, references, hover, implementation, document-symbols: the file that defines the symbol."),
          new ToolParameter("symbolName", ToolParameterType.Text,
              "Required for definition, references, hover, implementation, workspace-symbols: the symbol name. No line/column is accepted."),
          new ToolParameter("occurrence", ToolParameterType.WholeNumber,
              "Optional for position queries: 0-based occurrence index when the symbol occurs more than once (default 0)."),
          new ToolParameter("depth", ToolParameterType.WholeNumber,
              "Optional for call-hierarchy actions: 1 (default) or 2 levels."),
      ],
      [ToolTimeout.ParameterName, "action"]);

  private static string BuildDescription()
  {
    return "Query language-server symbol navigation through one dispatch surface."
        + " timeoutSeconds is mandatory. action is exactly one of " + string.Join(", ", ActionNames)
        + " (case-sensitive)."
        + " definition, references, hover, implementation, call-hierarchy-incoming, and call-hierarchy-outgoing require path and symbolName and take an optional occurrence (default 0); call-hierarchy actions take an optional depth (1 default, 2 max);"
        + " document-symbols requires path; workspace-symbols requires symbolName."
        + " The caller never supplies line or column: the tool resolves the position from the symbol name via document symbols."
        + " Multiple occurrences resolve deterministically and the result names the occurrence used."
        + " A symbol that is only referenced (not defined) in the named file returns Unsupported, never a guessed position."
        + " A member resolves by its declared name even when the server reports it with its signature."
        + " hover carries the server's hover text after the site line."
        + " The first call to a workspace connects to its language server lazily; later calls reuse the session."
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
    return Render(outcome, args);
  }

  private static LspSymbolCommand CommandOf(LspSymbolToolInput args, string? resolvedPath) => args.Action switch
  {
    LspSymbolAction.Definition => new LspSymbolCommand.Definition(resolvedPath!, args.SymbolName!, args.Occurrence),
    LspSymbolAction.References => new LspSymbolCommand.References(resolvedPath!, args.SymbolName!, args.Occurrence),
    LspSymbolAction.Hover => new LspSymbolCommand.Hover(resolvedPath!, args.SymbolName!, args.Occurrence),
    LspSymbolAction.DocumentSymbols => new LspSymbolCommand.DocumentSymbols(resolvedPath!),
    LspSymbolAction.WorkspaceSymbols => new LspSymbolCommand.WorkspaceSymbols(args.SymbolName!),
    LspSymbolAction.Implementation => new LspSymbolCommand.Implementation(resolvedPath!, args.SymbolName!, args.Occurrence),
    LspSymbolAction.CallHierarchyIncoming => new LspSymbolCommand.CallHierarchyIncoming(resolvedPath!, args.SymbolName!, args.Occurrence, args.Depth),
    LspSymbolAction.CallHierarchyOutgoing => new LspSymbolCommand.CallHierarchyOutgoing(resolvedPath!, args.SymbolName!, args.Occurrence, args.Depth),
    _ => throw new InvalidOperationException("unreachable: action is a validated enum"),
  };

  private static string ActionText(LspSymbolAction action) => action switch
  {
    LspSymbolAction.Definition => "definition",
    LspSymbolAction.References => "references",
    LspSymbolAction.Hover => "hover",
    LspSymbolAction.DocumentSymbols => "document-symbols",
    LspSymbolAction.WorkspaceSymbols => "workspace-symbols",
    LspSymbolAction.Implementation => "implementation",
    LspSymbolAction.CallHierarchyIncoming => "call-hierarchy-incoming",
    LspSymbolAction.CallHierarchyOutgoing => "call-hierarchy-outgoing",
    _ => "unknown",
  };

  private static ToolResult Render(LspSymbolOutcome outcome, LspSymbolToolInput args)
  {
    string actionText = ActionText(args.Action);
    return outcome switch
    {
      LspSymbolOutcome.Ready ready => RenderReady(ready, actionText, args),
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
    lines.Add(string.Create(CultureInfo.InvariantCulture,
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

  private static ToolResult Err(DomainError error) => new(
      $"Error [{error.Code}]: {error.Message}", true);
}
