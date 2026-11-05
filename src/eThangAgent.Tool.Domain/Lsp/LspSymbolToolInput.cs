using System.Text.Json;
using eThangAgent.SharedKernel;

namespace eThangAgent.ToolDomain.Lsp;

/// <summary>The 'lsp' tool's action switch.</summary>
public enum LspSymbolAction
{
  Definition,
  References,
  Hover,
  DocumentSymbols,
  WorkspaceSymbols,
  Implementation,
  CallHierarchyIncoming,
  CallHierarchyOutgoing,
  WorkspaceLoad,
}

/// <summary>Strictly parsed arguments for the 'lsp' symbol tool (issue #357): a required
///     action switch, path + symbolName (or file + occurrence index) for position queries,
///     path alone for document symbols, symbolName alone for workspace symbols. No
///     line/column is ever accepted from the caller.</summary>
#pragma warning disable IDE0046 // Named decision: guard+return names each validation step; the ternary form hides it.
public sealed record LspSymbolToolInput(LspSymbolAction Action, string? Path, string? SymbolName, int Occurrence, int Depth = 1)
{
  /// <summary>One row per action: its wire name, the arguments it accepts beyond action and
  ///     timeoutSeconds, and the parser that validates them. Adding an action is a row here,
  ///     never another switch over the same enum.</summary>
  private sealed record ActionRow(
      LspSymbolAction Action,
      string Name,
      string[] Arguments,
      Func<JsonElement, LspSymbolAction, Result<LspSymbolToolInput>> Parse);

  private static readonly ActionRow[] Rows =
  [
    new(LspSymbolAction.Definition, "definition", ["path", "symbolName", "occurrence"], ParsePositionQuery),
    new(LspSymbolAction.References, "references", ["path", "symbolName", "occurrence"], ParsePositionQuery),
    new(LspSymbolAction.Hover, "hover", ["path", "symbolName", "occurrence"], ParsePositionQuery),
    new(LspSymbolAction.DocumentSymbols, "document-symbols", ["path"], ParseDocumentSymbols),
    new(LspSymbolAction.WorkspaceSymbols, "workspace-symbols", ["symbolName"], ParseWorkspaceSymbols),
    new(LspSymbolAction.Implementation, "implementation", ["path", "symbolName", "occurrence"], ParsePositionQuery),
    new(LspSymbolAction.CallHierarchyIncoming, "call-hierarchy-incoming", ["path", "symbolName", "occurrence", "depth"], ParseCallHierarchy),
    new(LspSymbolAction.CallHierarchyOutgoing, "call-hierarchy-outgoing", ["path", "symbolName", "occurrence", "depth"], ParseCallHierarchy),
    new(LspSymbolAction.WorkspaceLoad, "workspace-load", [], NoArguments),
  ];

  private static readonly string[] ActionNames = BuildActionNames();

  private static string[] BuildActionNames() => [.. Rows.Select(row => row.Name)];

  private static ActionRow? FindRow(LspSymbolAction action)
    => Rows.FirstOrDefault(row => row.Action == action);

  private static ActionRow? FindRow(string name)
    => Rows.FirstOrDefault(row => string.Equals(row.Name, name, StringComparison.Ordinal));

  private static string ActionList() => string.Join(", ", ActionNames);

  public static Result<LspSymbolToolInput> Create(string jsonArguments)
  {
    Result<JsonElement> baseParse = ToolArguments.ParseObject(jsonArguments);
    if (!baseParse.IsSuccess)
    {
      return Result.Failure<LspSymbolToolInput>(baseParse.Error);
    }

    JsonElement json = baseParse.Value;
    Result<LspSymbolAction> action = ParseAction(json);
    if (!action.IsSuccess)
    {
      return Result.Failure<LspSymbolToolInput>(action.Error);
    }

    DomainError? unknown = ToolArguments.RejectUnknownParameters(json, Allowed(action.Value));
    if (unknown is not null)
    {
      return Result.Failure<LspSymbolToolInput>(unknown);
    }

    return Dispatch(json, action.Value);
  }

  private static Result<LspSymbolToolInput> Dispatch(JsonElement json, LspSymbolAction action)
    => FindRow(action) is ActionRow row
        ? row.Parse(json, action)
        : Result.Failure<LspSymbolToolInput>(new DomainError("InvalidAction", "'action' is not a known action."));

  private static Result<LspSymbolAction> ParseAction(JsonElement json)
  {
    if (!json.TryGetProperty("action", out JsonElement actionEl))
    {
      return Result.Failure<LspSymbolAction>(new DomainError("InvalidAction",
          "'action' is required: exactly one of " + ActionList() + " (case-sensitive)."));
    }

    if (actionEl.ValueKind is not JsonValueKind.String)
    {
      return Result.Failure<LspSymbolAction>(new DomainError(ToolErrorCodes.InvalidParameterType,
          $"'action' must be a string, but got {actionEl.ValueKind}."));
    }

    string actionText = actionEl.GetString()!;
    return MapActionText(actionText);
  }

  private static Result<LspSymbolAction> MapActionText(string actionText)
    => FindRow(actionText) is ActionRow row
        ? Result.Success(row.Action)
        : Result.Failure<LspSymbolAction>(new DomainError("InvalidAction",
            $"'action' must be exactly one of {ActionList()} (case-sensitive; got '{actionText}')"));

  private static Result<LspSymbolToolInput> ParsePositionQuery(JsonElement json, LspSymbolAction action)
  {
    Result<string> path = ToolArguments.RequireString(json, "path",
        "For this action, 'path' names the file that defines the symbol.");
    if (!path.IsSuccess)
    {
      return Result.Failure<LspSymbolToolInput>(path.Error);
    }

    Result<string> symbol = ToolArguments.RequireString(json, "symbolName",
        "For this action, 'symbolName' names the symbol (no line/column is accepted).");
    if (!symbol.IsSuccess)
    {
      return Result.Failure<LspSymbolToolInput>(symbol.Error);
    }

    Result<int?> occ = ToolArguments.OptionalInt(json, "occurrence");
    if (!occ.IsSuccess)
    {
      return Result.Failure<LspSymbolToolInput>(occ.Error);
    }

    int occurrence = occ.Value ?? 0;
    if (occurrence < 0)
    {
      return Result.Failure<LspSymbolToolInput>(new DomainError(ToolErrorCodes.InvalidParameterValue,
          $"'occurrence' must be >= 0 (got {occurrence})."));
    }

    return Result.Success(new LspSymbolToolInput(action, path.Value, symbol.Value, occurrence));
  }

  private static Result<LspSymbolToolInput> ParseCallHierarchy(JsonElement json, LspSymbolAction action)
  {
    Result<LspSymbolToolInput> pos = ParsePositionQuery(json, action);
    if (!pos.IsSuccess)
    {
      return pos;
    }

    Result<int?> depthOpt = ToolArguments.OptionalInt(json, "depth");
    if (!depthOpt.IsSuccess)
    {
      return Result.Failure<LspSymbolToolInput>(depthOpt.Error);
    }

    int depth = depthOpt.Value ?? 1;
    if (depth is < 1 or > 2)
    {
      return Result.Failure<LspSymbolToolInput>(new DomainError(ToolErrorCodes.InvalidParameterValue,
          $"'depth' must be 1 or 2 (got {depth})."));
    }

    return Result.Success(pos.Value with { Depth = depth });
  }

  private static Result<LspSymbolToolInput> ParseDocumentSymbols(JsonElement json, LspSymbolAction action)
  {
    Result<string> path = ToolArguments.RequireString(json, "path",
        "For document-symbols, 'path' names the file.");
    if (!path.IsSuccess)
    {
      return Result.Failure<LspSymbolToolInput>(path.Error);
    }

    return Result.Success(new LspSymbolToolInput(action, path.Value, null, 0));
  }

  private static Result<LspSymbolToolInput> ParseWorkspaceSymbols(JsonElement json, LspSymbolAction action)
  {
    Result<string> symbol = ToolArguments.RequireString(json, "symbolName",
        "For workspace-symbols, 'symbolName' names the symbol query.");
    if (!symbol.IsSuccess)
    {
      return Result.Failure<LspSymbolToolInput>(symbol.Error);
    }

    return Result.Success(new LspSymbolToolInput(action, null, symbol.Value, 0));
  }

  private static string[] Allowed(LspSymbolAction action)
  {
    ActionRow? row = FindRow(action);
    return row is null
        ? []
        : ["action", ToolTimeout.ParameterName, .. row.Arguments];
  }

  /// <summary>An action that takes no arguments beyond action and timeoutSeconds.</summary>
  private static Result<LspSymbolToolInput> NoArguments(JsonElement json, LspSymbolAction action)
    => Result.Success(new LspSymbolToolInput(action, null, null, 0));
}
#pragma warning restore IDE0046
