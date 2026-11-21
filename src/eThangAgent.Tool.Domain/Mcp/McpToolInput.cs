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

  /// <summary>Configure a new server and verify its launch.</summary>
  Add,

  /// <summary>Change a configured server and verify the resulting launch.</summary>
  Update,

  /// <summary>Delete a configured server.</summary>
  Remove,
}

/// <summary>Strictly parsed arguments for the 'mcp' tool: a required action switch
///     (exactly one of list, call, add, update, remove, case-sensitive), the keys that
///     action accepts and no others, and the action's own required fields. Unknown keys,
///     wrong kinds, and missing required keys are typed errors; nothing is coerced.
///     The collection defaults (args [], env {}, headers {}, scope global, gate none) are
///     the declared contract the tool's description states, not silent widening.</summary>
/// <param name="Action">The validated action.</param>
/// <param name="Server">The configured server name: required for call, update, and remove.</param>
/// <param name="Tool">The server-local tool name: required for call.</param>
/// <param name="Arguments">The call's arguments object; empty for other actions.</param>
/// <param name="Spec">The launch shape: required for add, null otherwise.</param>
/// <param name="Edit">The partial change: required for update, null otherwise.</param>
public sealed record McpToolInput(
    McpAction Action,
    string? Server,
    string? Tool,
    JsonElement Arguments,
    McpServerSpec? Spec,
    McpServerEdit? Edit)
{
  private const string ServerName = "server";
  private const string NameName = "name";
  private const string TransportName = "transport";
  private const string CommandName = "command";
  private const string ArgsName = "args";
  private const string EnvName = "env";
  private const string HeadersName = "headers";
  private const string ScopeName = "scope";
  private const string PinnedVersionName = "pinnedVersion";
  private const string GateName = "gate";

  private static readonly string[] TransportNames = ["stdio", "http"];
  private static readonly string[] ScopeNames = ["global", "workspace"];
  private static readonly string[] GateNames = ["none", "mutating"];

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
    return unknown is not null ? Result.Failure<McpToolInput>(unknown) : Dispatch(action.Value, json);
  }

  private static Result<McpToolInput> Dispatch(McpAction action, JsonElement json)
  {

    return action switch
    {
      McpAction.List => Result.Success(new McpToolInput(McpAction.List, null, null, EmptyObject(), null, null)),
      McpAction.Call => ParseCall(json),
      McpAction.Add => ParseAdd(json),
      McpAction.Update => ParseUpdate(json),
      McpAction.Remove => ParseRemove(json),
      _ => Result.Failure<McpToolInput>(new DomainError("InvalidAction", "unreachable: action is a validated enum.")),
    };
  }

  private static Result<McpAction> ParseAction(JsonElement json)
  {
    if (!json.TryGetProperty("action", out JsonElement actionEl))
    {
      return Result.Failure<McpAction>(new DomainError("InvalidAction",
          "'action' is required: exactly one of list, call, add, update, remove (case-sensitive)."));
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
      "add" => Result.Success(McpAction.Add),
      "update" => Result.Success(McpAction.Update),
      "remove" => Result.Success(McpAction.Remove),
      _ => Result.Failure<McpAction>(new DomainError("InvalidAction",
          $"'action' must be exactly one of list, call, add, update, remove (case-sensitive; got '{actionText}').")),
    };
  }

  private static Result<McpToolInput> ParseCall(JsonElement json)
  {
    Result<string> server = RequireServer(json, "For call, 'server' names the configured MCP server (see list).");
    if (!server.IsSuccess)
    {
      return Result.Failure<McpToolInput>(server.Error);
    }

    Result<string> tool = ToolArguments.RequireString(json, "tool",
        "For call, 'tool' names the tool on that server (see list).");
    if (!tool.IsSuccess)
    {
      return Result.Failure<McpToolInput>(tool.Error);
    }

    DomainError? argsError = ParseArguments(json, out JsonElement arguments);
    return argsError is not null
        ? Result.Failure<McpToolInput>(argsError)
        : Result.Success(new McpToolInput(McpAction.Call, server.Value, tool.Value, arguments, null, null));
  }

  private static Result<McpToolInput> ParseAdd(JsonElement json)
  {
    Result<string> name = RequireServer(json, "For add, 'server' is the new server's name.");
    if (!name.IsSuccess)
    {
      return Result.Failure<McpToolInput>(name.Error);
    }

    Result<string> command = ToolArguments.RequireString(json, CommandName,
        "For add, 'command' is the stdio launch command or the http endpoint URL.");
    if (!command.IsSuccess)
    {
      return Result.Failure<McpToolInput>(command.Error);
    }

    Result<McpTransport> transport = ParseChoice(json, TransportName, TransportNames,
        McpTransport.Stdio, [McpTransport.Stdio, McpTransport.Http]);
    if (!transport.IsSuccess)
    {
      return Result.Failure<McpToolInput>(transport.Error);
    }

    Result<LaunchFields> fields = ParseLaunchFields(json);
    if (!fields.IsSuccess)
    {
      return Result.Failure<McpToolInput>(fields.Error);
    }

    McpServerSpec spec = new(name.Value, transport.Value, command.Value,
        fields.Value.Args, fields.Value.Env, fields.Value.Headers,
        fields.Value.Scope, fields.Value.PinnedVersion, fields.Value.GateMode);
    return spec.Problem() is { } problem
        ? Result.Failure<McpToolInput>(new DomainError(ToolErrorCodes.InvalidParameterValue, problem))
        : Result.Success(new McpToolInput(McpAction.Add, name.Value, null, EmptyObject(), spec, null));
  }

  private static Result<McpToolInput> ParseUpdate(JsonElement json)
  {
    Result<string> server = RequireServer(json, "For update, 'server' names the configured server to change (see list).");
    if (!server.IsSuccess)
    {
      return Result.Failure<McpToolInput>(server.Error);
    }

    Result<EditFields> fields = ParseEditFields(json);
    if (!fields.IsSuccess)
    {
      return Result.Failure<McpToolInput>(fields.Error);
    }

    McpServerEdit edit = fields.Value.ToEdit();
    return edit.IsEmpty
        ? Result.Failure<McpToolInput>(new DomainError(ToolErrorCodes.MissingParameter,
            "For update, supply at least one field to change: name, transport, command, args, env, headers, scope, pinnedVersion, gate."))
        : Result.Success(new McpToolInput(McpAction.Update, server.Value, null, EmptyObject(), null, edit));
  }

  private static Result<McpToolInput> ParseRemove(JsonElement json)
  {
    Result<string> server = RequireServer(json, "For remove, 'server' names the configured server to delete (see list).");
    return server.IsSuccess
        ? Result.Success(new McpToolInput(McpAction.Remove, server.Value, null, EmptyObject(), null, null))
        : Result.Failure<McpToolInput>(server.Error);
  }

  private static Result<string> RequireServer(JsonElement json, string requirement) =>
      ToolArguments.RequireString(json, ServerName, requirement);

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

  /// <summary>The launch fields add requires in full: the collections (absent means
  ///     empty), the scope (absent means global), the pin (absent means none), and the
  ///     gate (absent means none).</summary>
  private sealed record LaunchFields(
      IReadOnlyList<string> Args,
      IReadOnlyDictionary<string, string> Env,
      IReadOnlyDictionary<string, string> Headers,
      McpServerScope Scope,
      string? PinnedVersion,
      McpGateMode GateMode);

  private static Result<LaunchFields> ParseLaunchFields(JsonElement json)
  {
    Result<Collections> collections = ParseCollections(json);
    if (!collections.IsSuccess)
    {
      return Result.Failure<LaunchFields>(collections.Error);
    }

    Result<McpServerScope> scope = ParseChoice(json, ScopeName, ScopeNames,
        McpServerScope.Global, [McpServerScope.Global, McpServerScope.Workspace]);
    if (!scope.IsSuccess)
    {
      return Result.Failure<LaunchFields>(scope.Error);
    }

    Result<McpGateMode> gate = ParseChoice(json, GateName, GateNames,
        McpGateMode.None, [McpGateMode.None, McpGateMode.Mutating]);
    if (!gate.IsSuccess)
    {
      return Result.Failure<LaunchFields>(gate.Error);
    }

    Result<string?> pinned = ToolArguments.OptionalString(json, PinnedVersionName);
    return pinned.IsSuccess
        ? Result.Success(new LaunchFields(collections.Value.Args, collections.Value.Env,
            collections.Value.Headers, scope.Value, PinnedOrNull(pinned.Value), gate.Value))
        : Result.Failure<LaunchFields>(pinned.Error);
  }

  /// <summary>The fields update may change, each null when the caller omitted it.</summary>
  private sealed record EditFields(
      string? Name,
      McpTransport? Transport,
      string? CommandOrUrl,
      Collections? Collections,
      McpServerScope? Scope,
      string? PinnedVersion,
      bool PinnedSupplied,
      McpGateMode? GateMode)
  {
    public McpServerEdit ToEdit() => new(
        Name,
        Transport,
        CommandOrUrl,
        Collections?.Args,
        Collections?.Env,
        Collections?.Headers,
        Scope,
        PinnedSupplied ? PinnedVersion ?? string.Empty : null,
        GateMode);
  }

  private static Result<EditFields> ParseEditFields(JsonElement json)
  {
    Result<string?> name = ToolArguments.OptionalString(json, NameName);
    if (!name.IsSuccess)
    {
      return Result.Failure<EditFields>(name.Error);
    }

    Result<string?> command = ToolArguments.OptionalString(json, CommandName);
    if (!command.IsSuccess)
    {
      return Result.Failure<EditFields>(command.Error);
    }

    Result<McpTransport?> transport = ParseOptionalChoice(json, TransportName, TransportNames,
        [McpTransport.Stdio, McpTransport.Http]);
    if (!transport.IsSuccess)
    {
      return Result.Failure<EditFields>(transport.Error);
    }

    Result<McpServerScope?> scope = ParseOptionalChoice(json, ScopeName, ScopeNames,
        [McpServerScope.Global, McpServerScope.Workspace]);
    if (!scope.IsSuccess)
    {
      return Result.Failure<EditFields>(scope.Error);
    }

    Result<McpGateMode?> gate = ParseOptionalChoice(json, GateName, GateNames,
        [McpGateMode.None, McpGateMode.Mutating]);
    if (!gate.IsSuccess)
    {
      return Result.Failure<EditFields>(gate.Error);
    }

    Result<Collections?> collections = ParseOptionalCollections(json);
    if (!collections.IsSuccess)
    {
      return Result.Failure<EditFields>(collections.Error);
    }

    Result<string?> pinned = ToolArguments.OptionalString(json, PinnedVersionName);
    if (!pinned.IsSuccess)
    {
      return Result.Failure<EditFields>(pinned.Error);
    }

    EditFields fields = new(Trimmed(name.Value), transport.Value, Trimmed(command.Value),
        collections.Value, scope.Value, pinned.Value, json.TryGetProperty(PinnedVersionName, out _), gate.Value);
    return Result.Success(fields);
  }

  /// <summary>The three collection fields add requires, each absent meaning empty.</summary>
  private sealed record Collections(
      IReadOnlyList<string> Args,
      IReadOnlyDictionary<string, string> Env,
      IReadOnlyDictionary<string, string> Headers);

  private static Result<Collections> ParseCollections(JsonElement json)
  {
    Result<Collections?> optional = ParseOptionalCollections(json);
    return !optional.IsSuccess
        ? Result.Failure<Collections>(optional.Error)
        : Result.Success(optional.Value ?? new Collections([], EmptyMap(), EmptyMap()));
  }

  private static Result<Collections?> ParseOptionalCollections(JsonElement json)
  {
    Result<IReadOnlyList<string>?> args = ToolArguments.OptionalStringArray(json, ArgsName);
    if (!args.IsSuccess)
    {
      return Result.Failure<Collections?>(args.Error);
    }

    Result<IReadOnlyDictionary<string, string>?> env = OptionalStringMap(json, EnvName);
    if (!env.IsSuccess)
    {
      return Result.Failure<Collections?>(env.Error);
    }

    Result<IReadOnlyDictionary<string, string>?> headers = OptionalStringMap(json, HeadersName);
    return headers.IsSuccess
        ? Combine(args.Value, env.Value, headers.Value)
        : Result.Failure<Collections?>(headers.Error);
  }

  private static Result<Collections?> Combine(IReadOnlyList<string>? args,
      IReadOnlyDictionary<string, string>? env, IReadOnlyDictionary<string, string>? headers)
  {

    return args is null && env is null && headers is null
        ? Result.Success<Collections?>(null)
        : Result.Success<Collections?>(new Collections(args ?? [], env ?? EmptyMap(),
            headers ?? EmptyMap()));
  }

  /// <summary>One optional JSON object whose values must all be strings - the env and
  ///     headers column shapes. A present non-object, or any non-string value, is a typed
  ///     error naming what was found.</summary>
  private static Result<IReadOnlyDictionary<string, string>?> OptionalStringMap(JsonElement json, string name)
  {
    if (!json.TryGetProperty(name, out JsonElement el))
    {
      return Result.Success<IReadOnlyDictionary<string, string>?>(null);
    }

    if (el.ValueKind != JsonValueKind.Object)
    {
      return Result.Failure<IReadOnlyDictionary<string, string>?>(new DomainError(ToolErrorCodes.InvalidParameterType,
          $"'{name}' must be an object of string values, but got {el.ValueKind}."));
    }

    Dictionary<string, string> entries = [];
    foreach (JsonProperty property in el.EnumerateObject())
    {
      if (property.Value.ValueKind != JsonValueKind.String)
      {
        return Result.Failure<IReadOnlyDictionary<string, string>?>(new DomainError(ToolErrorCodes.InvalidParameterType,
            $"'{name}.{property.Name}' must be a string, but got {property.Value.ValueKind}."));
      }

      entries[property.Name] = property.Value.GetString()!;
    }

    return Result.Success<IReadOnlyDictionary<string, string>?>(entries);
  }

  private static Result<T> ParseChoice<T>(JsonElement json, string name, string[] names,
      T fallback, IReadOnlyList<T> values)
      where T : struct
  {
    return !json.TryGetProperty(name, out _)
        ? Result.Success(fallback)
        : ParseChosen(ParseOptionalChoice(json, name, names, values), name, names, json);
  }

  private static Result<T> ParseChosen<T>(Result<T?> parsed, string name, string[] names,
      JsonElement json)
      where T : struct
  {
    return parsed is { IsSuccess: true, Value: { } chosen }
        ? Result.Success(chosen)
        : Result.Failure<T>(ChoiceError(name, names, json.GetProperty(name)));
  }

  private static Result<T?> ParseOptionalChoice<T>(JsonElement json, string name, string[] names,
      IReadOnlyList<T> values)
      where T : struct
  {
    if (!json.TryGetProperty(name, out JsonElement el))
    {
      return Result.Success<T?>(null);
    }

    if (el.ValueKind != JsonValueKind.String)
    {
      return Result.Failure<T?>(new DomainError(ToolErrorCodes.InvalidParameterType,
          $"'{name}' must be a string, but got {el.ValueKind}."));
    }

    string text = el.GetString()!;
    for (int i = 0; i < names.Length; i++)
    {
      if (string.Equals(names[i], text, StringComparison.Ordinal))
      {
        return Result.Success<T?>(values[i]);
      }
    }

    return Result.Failure<T?>(ChoiceError(name, names, el));
  }

  private static DomainError ChoiceError(string name, string[] names, JsonElement el) =>
      el.ValueKind == JsonValueKind.String
          ? new DomainError(ToolErrorCodes.InvalidParameterValue,
              $"'{name}' must be exactly one of {string.Join(", ", names)} (case-sensitive), but got '{el.GetString()}'.")
          : new DomainError(ToolErrorCodes.InvalidParameterType,
              $"'{name}' must be a string, but got {el.ValueKind}.");

  private static string? Trimmed(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();

  private static string? PinnedOrNull(string? text) => Trimmed(text);

  private static Dictionary<string, string> EmptyMap() => [];

  private static string[] Allowed(McpAction action) => action switch
  {
    McpAction.List => ["action", ToolTimeout.ParameterName],
    McpAction.Call => ["action", ToolTimeout.ParameterName, ServerName, "tool", "arguments"],
    McpAction.Add => ["action", ToolTimeout.ParameterName, ServerName, TransportName, CommandName,
        ArgsName, EnvName, HeadersName, ScopeName, PinnedVersionName, GateName],
    McpAction.Update => ["action", ToolTimeout.ParameterName, ServerName, NameName, TransportName,
        CommandName, ArgsName, EnvName, HeadersName, ScopeName, PinnedVersionName, GateName],
    McpAction.Remove => ["action", ToolTimeout.ParameterName, ServerName],
    _ => [],
  };

  private static JsonElement EmptyObject()
  {
    using JsonDocument doc = JsonDocument.Parse("{}");
    return doc.RootElement.Clone();
  }
}
