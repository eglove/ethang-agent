using System.Text.Json;
using eThangAgent.SharedKernel;

namespace eThangAgent.ToolDomain.Mcp;

/// <summary>The 'mcp' tool's action switch.</summary>
public enum McpAction
{
  /// <summary>List configured servers with connection state; connects nothing.</summary>
  List,

  /// <summary>Call one tool on one server.</summary>
  Call,
}

/// <summary>Strictly parsed arguments for the 'mcp' tool (issue #104): a required
///     action switch (exactly one of list, call, case-sensitive), server/tool required
///     for call, and an optional arguments object (default {}). Unknown keys, wrong
///     kinds, and missing required keys are typed errors; nothing is coerced.</summary>
public sealed record McpToolInput(McpAction Action, string? Server, string? Tool, JsonElement Arguments)
{
  /// <summary>Parses raw JSON arguments into validated input.</summary>
  public static Result<McpToolInput> Create(string jsonArguments)
  {
    Result<JsonElement> baseParse = ToolArguments.ParseObject(jsonArguments);
    if (!baseParse.IsSuccess)
    {
      return Result.Failure<McpToolInput>(baseParse.Error);
    }

    JsonElement json = baseParse.Value;
    Result<McpAction> action = ParseAction(json);
    if (!action.IsSuccess)
    {
      return Result.Failure<McpToolInput>(action.Error);
    }

    DomainError? unknown = ToolArguments.RejectUnknownParameters(json, Allowed(action.Value));
    if (unknown is not null)
    {
      return Result.Failure<McpToolInput>(unknown);
    }

    JsonElement empty = EmptyObject();
    Result<McpToolInput> parsed = action.Value == McpAction.Call
        ? ParseCall(json, action.Value)
        : Result.Success(new McpToolInput(action.Value, null, null, empty));
    return parsed;
  }

  private static Result<McpAction> ParseAction(JsonElement json)
  {
    if (!json.TryGetProperty("action", out JsonElement actionEl))
    {
      return Result.Failure<McpAction>(new DomainError("InvalidAction",
          "'action' is required: exactly one of list, call (case-sensitive)."));
    }

    if (actionEl.ValueKind != JsonValueKind.String)
    {
      return Result.Failure<McpAction>(new DomainError(ToolErrorCodes.InvalidParameterType,
          $"'action' must be a string, but got {actionEl.ValueKind}."));
    }

    string actionText = actionEl.GetString()!;
    return actionText switch
    {
      "list" => Result.Success(McpAction.List),
      "call" => Result.Success(McpAction.Call),
      _ => Result.Failure<McpAction>(new DomainError("InvalidAction",
          $"'action' must be exactly one of list, call (case-sensitive; got '{actionText}').")),
    };
  }

  private static Result<McpToolInput> ParseCall(JsonElement json, McpAction action)
  {
    Result<string> serverText = ToolArguments.RequireString(json, "server",
        "For call, 'server' names the configured MCP server (see list).");
    if (!serverText.IsSuccess)
    {
      return Result.Failure<McpToolInput>(serverText.Error);
    }

    Result<string> toolText = ToolArguments.RequireString(json, "tool",
        "For call, 'tool' names the tool on that server (see list).");
    if (!toolText.IsSuccess)
    {
      return Result.Failure<McpToolInput>(toolText.Error);
    }

    DomainError? argsError = ParseArguments(json, out JsonElement arguments);
    if (argsError is not null)
    {
      return Result.Failure<McpToolInput>(argsError);
    }

    McpToolInput input = new(action, serverText.Value, toolText.Value, arguments);
    return Result.Success(input);
  }

  private static DomainError? ParseArguments(JsonElement json, out JsonElement arguments)
  {
    arguments = EmptyObject();
    if (!json.TryGetProperty("arguments", out JsonElement argsEl))
    {
      return null;
    }

    if (argsEl.ValueKind != JsonValueKind.Object)
    {
      return new DomainError(ToolErrorCodes.InvalidParameterType,
          $"'arguments' must be an object, but got {argsEl.ValueKind}.");
    }

    arguments = argsEl.Clone();
    return null;
  }

  private static string[] Allowed(McpAction action) => action switch
  {
    McpAction.List => ["action", ToolTimeout.ParameterName],
    McpAction.Call => ["action", ToolTimeout.ParameterName, "server", "tool", "arguments"],
    _ => [],
  };

  private static JsonElement EmptyObject()
  {
    using JsonDocument doc = JsonDocument.Parse("{}");
    return doc.RootElement.Clone();
  }
}
