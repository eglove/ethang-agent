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
    if (!json.TryGetProperty("action", out JsonElement actionEl))
    {
      return Result.Failure<McpToolInput>(new DomainError("InvalidAction",
          "'action' is required: exactly one of list, call (case-sensitive)."));
    }

    if (actionEl.ValueKind != JsonValueKind.String)
    {
      return Result.Failure<McpToolInput>(new DomainError(ToolErrorCodes.InvalidParameterType,
          $"'action' must be a string, but got {actionEl.ValueKind}."));
    }

    string actionText = actionEl.GetString()!;
    Result<McpAction> action = actionText switch
    {
      "list" => Result.Success(McpAction.List),
      "call" => Result.Success(McpAction.Call),
      _ => Result.Failure<McpAction>(new DomainError("InvalidAction",
          $"'action' must be exactly one of list, call (case-sensitive; got '{actionText}').")),
    };
    if (!action.IsSuccess)
    {
      return Result.Failure<McpToolInput>(action.Error);
    }

    DomainError? unknown = ToolArguments.RejectUnknownParameters(json, Allowed(action.Value));
    if (unknown is not null)
    {
      return Result.Failure<McpToolInput>(unknown);
    }

    string? server = null;
    string? tool = null;
    JsonElement arguments = EmptyObject();
    if (action.Value == McpAction.Call)
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

      if (json.TryGetProperty("arguments", out JsonElement argsEl))
      {
        if (argsEl.ValueKind != JsonValueKind.Object)
        {
          return Result.Failure<McpToolInput>(new DomainError(ToolErrorCodes.InvalidParameterType,
              $"'arguments' must be an object, but got {argsEl.ValueKind}."));
        }

        arguments = argsEl.Clone();
      }

      server = serverText.Value;
      tool = toolText.Value;
    }

    return Result.Success(new McpToolInput(action.Value, server, tool, arguments));
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
