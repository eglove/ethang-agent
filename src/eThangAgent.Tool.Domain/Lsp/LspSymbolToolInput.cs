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
}

/// <summary>Strictly parsed arguments for the 'lsp' symbol tool (issue #357): a required
///     action switch, path + symbolName (or file + occurrence index) for position queries,
///     path alone for document symbols, symbolName alone for workspace symbols. No
///     line/column is ever accepted from the caller.</summary>
#pragma warning disable IDE0046 // Named decision: guard+return names each validation step; the ternary form hides it.
public sealed record LspSymbolToolInput(LspSymbolAction Action, string? Path, string? SymbolName, int Occurrence)
{
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

  private static Result<LspSymbolToolInput> Dispatch(JsonElement json, LspSymbolAction action) => action switch
  {
    LspSymbolAction.DocumentSymbols => ParseDocumentSymbols(json, action),
    LspSymbolAction.WorkspaceSymbols => ParseWorkspaceSymbols(json, action),
    LspSymbolAction.Definition => ParsePositionQuery(json, action),
    LspSymbolAction.References => ParsePositionQuery(json, action),
    LspSymbolAction.Hover => ParsePositionQuery(json, action),
    LspSymbolAction.Implementation => ParsePositionQuery(json, action),
    _ => Result.Failure<LspSymbolToolInput>(new DomainError("InvalidAction", "'action' is not a known action.")),
  };

  private static Result<LspSymbolAction> ParseAction(JsonElement json)
  {
    if (!json.TryGetProperty("action", out JsonElement actionEl))
    {
      return Result.Failure<LspSymbolAction>(new DomainError("InvalidAction",
          "'action' is required: exactly one of definition, references, hover, document-symbols, workspace-symbols, implementation (case-sensitive)."));
    }

    if (actionEl.ValueKind != JsonValueKind.String)
    {
      return Result.Failure<LspSymbolAction>(new DomainError(ToolErrorCodes.InvalidParameterType,
          $"'action' must be a string, but got {actionEl.ValueKind}."));
    }

    string actionText = actionEl.GetString()!;
    return actionText switch
    {
      "definition" => Result.Success(LspSymbolAction.Definition),
      "references" => Result.Success(LspSymbolAction.References),
      "hover" => Result.Success(LspSymbolAction.Hover),
      "document-symbols" => Result.Success(LspSymbolAction.DocumentSymbols),
      "workspace-symbols" => Result.Success(LspSymbolAction.WorkspaceSymbols),
      "implementation" => Result.Success(LspSymbolAction.Implementation),
      _ => Result.Failure<LspSymbolAction>(new DomainError("InvalidAction",
          $"'action' must be exactly one of definition, references, hover, document-symbols, workspace-symbols, implementation (case-sensitive; got '{actionText}').")),
    };
  }

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

  private static string[] Allowed(LspSymbolAction action) => action switch
  {
    LspSymbolAction.DocumentSymbols => ["action", ToolTimeout.ParameterName, "path"],
    LspSymbolAction.WorkspaceSymbols => ["action", ToolTimeout.ParameterName, "symbolName"],
    LspSymbolAction.Definition => ["action", ToolTimeout.ParameterName, "path", "symbolName", "occurrence"],
    LspSymbolAction.References => ["action", ToolTimeout.ParameterName, "path", "symbolName", "occurrence"],
    LspSymbolAction.Hover => ["action", ToolTimeout.ParameterName, "path", "symbolName", "occurrence"],
    LspSymbolAction.Implementation => ["action", ToolTimeout.ParameterName, "path", "symbolName", "occurrence"],
    _ => [],
  };
}
#pragma warning restore IDE0046
