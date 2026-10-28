using System.Globalization;
using System.Text;
using System.Text.Json;
using eThangAgent.CapabilityDomain;
using eThangAgent.SharedKernel;

namespace eThangAgent.StateDomain;

public sealed class StateCapabilityProvider(IStateService service) : ICapabilityProvider
{
  public const string ProviderId = "state";

  private const string StartLine = "startLine";
  private const string EndLine = "endLine";
  private const string ExpectedVersion = "expectedVersion";
  private const string Limit = "limit";
  private const string Prefix = "prefix";

  private readonly IStateService _service = service ?? throw new ArgumentNullException(nameof(service));

  public string Id => ProviderId;

  public IReadOnlyList<ActionDescriptor> Actions { get; } =
  [
      new("get", "Read a durable state value, or a line range of it.",
            "Reads one namespaced key. Fails with KeyNotFound when absent. Optional startLine/endLine return only that range under an envelope '[<key> v<version> | lines <S>-<E> of <T>]'; an endLine past the last line is clamped with a visible '[note] ... clamped.' warning. Range errors: both-or-neither, startLine >= 1, endLine >= startLine — violations are InvalidActionInput.",
            [new ActionParameter("key", ActionParameterTypes.StringType, "Namespaced key, e.g. current/head."),
             new ActionParameter(StartLine, ActionParameterTypes.IntegerType, "Optional, only together with endLine. First line to read, >= 1."),
             new ActionParameter(EndLine, ActionParameterTypes.IntegerType, "Optional, only together with startLine. Last line to read.")]),
        new("set", "Write a durable state value with optional compare-and-swap.",
            "Creates or updates a key. Supply expectedVersion to require the current version; a mismatch fails closed with VersionConflict naming the current version. Returns the new version.",
            [new ActionParameter("key", ActionParameterTypes.StringType, "Namespaced key."),
             new ActionParameter("value", ActionParameterTypes.StringType, "Value to store."),
             new ActionParameter(ExpectedVersion, ActionParameterTypes.IntegerType, "Optional. Fail unless the stored version matches.")]),
        new("delete", "Delete a durable state key.",
            "Removes a key. Supply expectedVersion for a compare-and-swap delete.",
            [new ActionParameter("key", ActionParameterTypes.StringType, "Namespaced key."),
             new ActionParameter(ExpectedVersion, ActionParameterTypes.IntegerType, "Optional. Fail unless the stored version matches.")]),
        new("list", "List state keys.",
            "Lists keys as 'ns/name v<version>' lines, optionally filtered by namespace.",
            [new ActionParameter("ns", ActionParameterTypes.StringType, "Optional namespace filter.")]),
        new("find", "Full-text search over workspace state values and key names.",
            "Searches all state in this workspace with SQLite FTS5 over values, namespaces, and key names. Output contract: header line '[state.find '<query>'] <N> hit(s)', then per hit the key as ns/name and an indented snippet line. Zero hits prints only the header. Malformed queries fail with InvalidQuery rather than returning empty. Errors: InvalidActionInput (bad params), InvalidLimit, InvalidQuery.",
            [new ActionParameter("query", ActionParameterTypes.StringType, "Required. FTS5 query text (supports prefix*, AND/OR/NOT)."),
             new ActionParameter(Limit, ActionParameterTypes.IntegerType, "Optional. Max hits, 1..100, default 20.")]),
        new("append", "Append one line to a state value atomically.",
            "CAS append: the line is added to the key's stored value with a newline separator. A missing key is created holding just the line. Text must be a single line without leading or trailing whitespace (InvalidText otherwise). Fails closed with VersionConflict when expectedVersion does not match — re-get, reconcile, retry; never blind-overwrite. This is how SDD ledgers are maintained. Errors: InvalidText (multi-line or padded text), VersionConflict (re-get, reconcile, retry), InvalidActionInput.",
            [new ActionParameter("key", ActionParameterTypes.StringType, "Namespaced key, e.g. sdd.my-plan/ledger."),
             new ActionParameter("text", ActionParameterTypes.StringType, "The single line to append."),
             new ActionParameter(ExpectedVersion, ActionParameterTypes.IntegerType, "Optional. Fail unless the stored version matches.")]),
        new("prune", "Bulk-delete every state key under a namespace prefix.",
            "Deletes all keys whose namespace equals the prefix or starts with '<prefix>.'. The dotted boundary is respected: prefix 'sdd.alpha' does not touch 'sdd.alphabeta'. The reserved namespace 'current' is rejected. Returns '[prune <prefix>] <N> key(s) removed'. Errors: ReservedNamespace ('current'), InvalidKey (malformed prefix), InvalidActionInput. Intended for cleaning up SDD task briefs and reports after a plan finishes.",
            [new ActionParameter(Prefix, ActionParameterTypes.StringType, "Namespace prefix, e.g. sdd.my-plan.")]),
        new("transition", "Attach a claim with evidence (stored, never run on attach).",
            "Records a labeled move from one world-state to another with summary and evidence commands. Evidence is replayable but has NOT run. Returns the transition id; status starts pending.",
            [new ActionParameter("from", ActionParameterTypes.StringType, "Prior state label."),
             new ActionParameter("to", ActionParameterTypes.StringType, "New state label."),
             new ActionParameter("summary", ActionParameterTypes.StringType, "What this claim asserts."),
             new ActionParameter("evidence", "String[]", "C# boolean expressions that, when evaluated, should confirm the claim.")]),
        new("verify", "Run attached evidence fail-closed and certify.",
            "Runs the evidence for the selected transitions (default: all pending) fail-closed. Certifies only when every command confirms; otherwise reports violated with blocking reasons and revokes any head certificate first.",
            [new ActionParameter("ids", "String[]", "Optional transition ids; default all pending.")]),
        new("checkgoal", "Run the goal/check commands and report.",
            "Runs the commands stored at goal/check and reports pass/fail. Report-only — no certification.",
            []),
        new("history", "Replay the state timeline.",
            "Returns the most recent timeline events (transitions, certified, violated).",
            [new ActionParameter(Limit, ActionParameterTypes.IntegerType, "Optional. Default 20.")]),
    ];

  public async Task<CapabilityInvocationResult> InvokeAsync(
      string actionName, string jsonArguments, CancellationToken ct = default)
  {
    try
    {
      return await DispatchAsync(actionName, jsonArguments).ConfigureAwait(false);
    }
    catch (StateInputException ex)
    {
      return CapabilityInvocationResult.Fail($"Error [InvalidActionInput]: {ex.Message}");
    }
  }

  /// <summary>Two-level dispatch: the outer switch halves the action vocabulary,
  ///     the inner switches resolve each half (one flat switch would exceed the
  ///     cyclomatic-complexity gate).</summary>
  private Task<CapabilityInvocationResult> DispatchAsync(string actionName, string jsonArguments)
      => actionName switch
      {
        "get" or "find" or "append" or "prune" or "set" or "delete" => DispatchFirstHalfAsync(actionName, jsonArguments),
        _ => DispatchSecondHalfAsync(actionName, jsonArguments),
      };

  private async Task<CapabilityInvocationResult> DispatchFirstHalfAsync(string actionName, string jsonArguments)
      => actionName switch
      {
        "get" => await GetAsync(jsonArguments).ConfigureAwait(false),
        "find" => await SearchAsync(jsonArguments).ConfigureAwait(false),
        "append" => await AppendAsync(jsonArguments).ConfigureAwait(false),
        "prune" => await PruneAsync(jsonArguments).ConfigureAwait(false),
        "set" => await SetAsync(jsonArguments).ConfigureAwait(false),
        _ => await DeleteAsync(jsonArguments).ConfigureAwait(false),
      };

  private async Task<CapabilityInvocationResult> DispatchSecondHalfAsync(string actionName, string jsonArguments)
      => actionName switch
      {
        "list" => await ListAsync(jsonArguments).ConfigureAwait(false),
        "transition" => await TransitionAsync(jsonArguments).ConfigureAwait(false),
        "verify" => await VerifyAsync(jsonArguments).ConfigureAwait(false),
        "checkgoal" => await CheckGoalAsync().ConfigureAwait(false),
        "history" => await HistoryAsync(jsonArguments).ConfigureAwait(false),
        _ => CapabilityInvocationResult.Fail($"Error [UnknownAction]: Unknown action: {actionName}."),
      };

  private async Task<CapabilityInvocationResult> GetAsync(string json)
  {
    Dictionary<string, JsonElement> args = ParseArgs(json, Allowed("key", StartLine, EndLine));
    string key = ReqString(args, "key");
    if (!args.ContainsKey(StartLine) && !args.ContainsKey(EndLine))
    {
      return ToResult(await _service.GetAsync(key).ConfigureAwait(false));
    }

    (int start, int end) = ParseLineRange(args);
    Result<string> value = await _service.GetAsync(key).ConfigureAwait(false);
    return !value.IsSuccess
        ? Gutter(value.Error)
        : await RenderRange(key, value.Value, start, end).ConfigureAwait(false);
  }

  /// <summary>Strict range validation: both-or-neither, integers, start >= 1,
  ///     end >= start.</summary>
  private static (int Start, int End) ParseLineRange(Dictionary<string, JsonElement> args)
  {
    int start = ReqRangeInt(args, StartLine, $"'{StartLine}' is required together with '{EndLine}' and must be an integer.");
    int end = ReqRangeInt(args, EndLine, $"'{EndLine}' is required together with '{StartLine}' and must be an integer.");
    ValidateRange(start, end);
    return (start, end);
  }

  private static void ValidateRange(int start, int end)
  {
    if (start < 1)
    {
      throw new StateInputException($"'{StartLine}' must be >= 1.");
    }

    if (end < start)
    {
      throw new StateInputException($"'{EndLine}' must be >= '{StartLine}'.");
    }
  }

  private static int ReqRangeInt(Dictionary<string, JsonElement> args, string name, string failure)
  {
    return !args.TryGetValue(name, out JsonElement element) || element.ValueKind != JsonValueKind.Number
            || !element.TryGetInt32(out int value)
        ? throw new StateInputException(failure)
        : value;
  }

  /// <summary>Renders the sliced lines under the envelope header; an end past the
  ///     last line clamps with a visible note. The version, when resolvable, comes
  ///     from ListAsync(ns) so IStateService signatures stay untouched.</summary>
  private async Task<CapabilityInvocationResult> RenderRange(string key, string stored, int start, int end)
  {
    string[] lines = stored.Split('\n');
    int total = lines.Length;
    int clampedEnd = Math.Min(end, total);
    string slice = string.Join("\n", lines[(start - 1)..clampedEnd]);
    string? versionPart = await ResolveVersionPart(key).ConfigureAwait(false);
    string header = versionPart is null
            ? $"[{key} | lines {start}-{clampedEnd} of {total}]"
            : $"[{key} {versionPart} | lines {start}-{clampedEnd} of {total}]";
    string output = header + "\n" + slice;
    if (end > total)
    {
      output += $"\n[note] endLine {end} exceeds last line {total}; clamped.";
    }

    return CapabilityInvocationResult.Ok(output);
  }

  private async Task<string?> ResolveVersionPart(string key)
  {
    int slashAt = key.IndexOf('/', StringComparison.Ordinal);
    Result<IReadOnlyList<string>> keys = await _service.ListAsync(key[..slashAt]).ConfigureAwait(false);
    if (!keys.IsSuccess)
    {
      return null;
    }

    string prefix = key + " v";
    string? match = keys.Value.FirstOrDefault(k => k.StartsWith(prefix, StringComparison.Ordinal));
    return match is not null ? "v" + match[prefix.Length..] : null;
  }

  private async Task<CapabilityInvocationResult> SearchAsync(string json)
  {
    Dictionary<string, JsonElement> args = ParseArgs(json, Allowed("query", Limit));
    string query = ReqString(args, "query");
    int limit = OptInt(args, Limit) ?? 20;
    Result<IReadOnlyList<StateSearchHit>> result = await _service.SearchAsync(query, limit).ConfigureAwait(false);
    if (!result.IsSuccess)
    {
      return Gutter(result.Error);
    }

    StringBuilder sb = new();
    _ = sb.Append(CultureInfo.InvariantCulture, $"[state.find '{query}'] {result.Value.Count} hit(s)");
    foreach (StateSearchHit hit in result.Value)
    {
      _ = sb.Append(CultureInfo.InvariantCulture, $"\n{hit.Ns}/{hit.Name}");
      _ = sb.Append(CultureInfo.InvariantCulture, $"\n  {hit.Snippet}");
    }
    return CapabilityInvocationResult.Ok(sb.ToString());
  }

  private async Task<CapabilityInvocationResult> SetAsync(string json)
  {
    Dictionary<string, JsonElement> args = ParseArgs(json, Allowed("key", "value", ExpectedVersion));
    Result<StateKeyValue> saved = await _service.SetAsync(ReqString(args, "key"), ReqString(args, "value"), OptInt(args, ExpectedVersion)).ConfigureAwait(false);
    return saved.IsSuccess
        ? CapabilityInvocationResult.Ok($"saved {saved.Value.Ns}/{saved.Value.Name} v{saved.Value.Version}")
        : Gutter(saved.Error);
  }

  private async Task<CapabilityInvocationResult> DeleteAsync(string json)
  {
    Dictionary<string, JsonElement> args = ParseArgs(json, Allowed("key", ExpectedVersion));
    return ToResult(await _service.DeleteAsync(
        ReqString(args, "key"), OptInt(args, ExpectedVersion)).ConfigureAwait(false));
  }

  private async Task<CapabilityInvocationResult> ListAsync(string json)
      => ToResult(await _service.ListAsync(OptString(ParseArgs(json, Allowed("ns")), "ns")).ConfigureAwait(false));

  private async Task<CapabilityInvocationResult> TransitionAsync(string json)
  {
    Dictionary<string, JsonElement> args = ParseArgs(json, Allowed("from", "to", "summary", "evidence"));
    return ToResult(await _service.TransitionAsync(
        ReqString(args, "from"), ReqString(args, "to"), ReqString(args, "summary"),
        OptStringArray(args, "evidence")).ConfigureAwait(false));
  }

  private async Task<CapabilityInvocationResult> VerifyAsync(string json)
  {
    CertificationReport report = await _service.VerifyAsync(OptStringArray(ParseArgs(json, Allowed("ids")), "ids")).ConfigureAwait(false);
    return Report(report);
  }

  private async Task<CapabilityInvocationResult> CheckGoalAsync()
      => Report(await _service.CheckGoalAsync().ConfigureAwait(false));

  private async Task<CapabilityInvocationResult> HistoryAsync(string json)
      => ToResult(await _service.HistoryAsync(OptInt(ParseArgs(json, Allowed(Limit)), Limit) ?? 20).ConfigureAwait(false));

  private async Task<CapabilityInvocationResult> AppendAsync(string json)
  {
    Dictionary<string, JsonElement> args = ParseArgs(json, Allowed("key", "text", ExpectedVersion));
    Result<StateKeyValue> saved = await _service.AppendAsync(ReqString(args, "key"), ReqString(args, "text"), OptInt(args, ExpectedVersion)).ConfigureAwait(false);
    return saved.IsSuccess
        ? CapabilityInvocationResult.Ok($"appended to {saved.Value.Ns}/{saved.Value.Name} v{saved.Value.Version}")
        : Gutter(saved.Error);
  }

  private async Task<CapabilityInvocationResult> PruneAsync(string json)
  {
    Dictionary<string, JsonElement> args = ParseArgs(json, Allowed(Prefix));
    Result<int> result = await _service.DeletePrefixAsync(ReqString(args, Prefix)).ConfigureAwait(false);
    return !result.IsSuccess
          ? Gutter(result.Error)
          : CapabilityInvocationResult.Ok($"[prune {ReqString(args, Prefix)}] {result.Value} key(s) removed");
  }

  /// <summary>A list result renders as its elements (one per line) — never the C#
  ///     ToString of the collection object, which would leak
  ///     '&lt;&gt;z__ReadOnlyList`1[...]' to the model (seen via state.list).</summary>
  private static CapabilityInvocationResult ToResult<T>(Result<T> result)
      => !result.IsSuccess
          ? Gutter(result.Error)
          : Render(result.Value);

  private static CapabilityInvocationResult Render<T>(T value)
      => value switch
      {
        null => CapabilityInvocationResult.Ok(""),
        IEnumerable<string> lines => CapabilityInvocationResult.Ok(string.Join("\n", lines)),
        _ => CapabilityInvocationResult.Ok(value.ToString() ?? ""),
      };

  private static CapabilityInvocationResult Gutter(DomainError error)
      => CapabilityInvocationResult.Fail($"Error [{error.Code}]: {error.Message}");

  private static CapabilityInvocationResult Report(CertificationReport report)
      => CapabilityInvocationResult.Ok(JsonSerializer.Serialize(report));

  private static HashSet<string> Allowed(params string[] names) => new(names, StringComparer.Ordinal);

  private static Dictionary<string, JsonElement> ParseArgs(string json, HashSet<string> allowed)
  {
    JsonElement root;
    try
    {
      using JsonDocument doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json);
      root = doc.RootElement.Clone();
    }
    catch (JsonException ex)
    {
      throw new StateInputException($"Arguments are not valid JSON: {ex.Message}");
    }
    if (root.ValueKind != JsonValueKind.Object)
    {
      throw new StateInputException("Arguments must be a JSON object.");
    }

    Dictionary<string, JsonElement> args = new(StringComparer.Ordinal);
    foreach (JsonProperty property in root.EnumerateObject())
    {
      if (!allowed.Contains(property.Name))
      {
        throw new StateInputException($"Unknown parameter '{property.Name}'.");
      }

      args[property.Name] = property.Value.Clone();
    }
    return args;
  }

  private static string ReqString(Dictionary<string, JsonElement> args, string name)
  {
    return !args.TryGetValue(name, out JsonElement element)
            || element.ValueKind != JsonValueKind.String
            || string.IsNullOrEmpty(element.GetString())
          ? throw new StateInputException($"'{name}' is required and must be a non-empty string.")
          : element.GetString()!;
  }

  private static string? OptString(Dictionary<string, JsonElement> args, string name)
        => args.TryGetValue(name, out JsonElement element) && element.ValueKind == JsonValueKind.String
            ? element.GetString()
            : null;

  private static int? OptInt(Dictionary<string, JsonElement> args, string name)
      => args.TryGetValue(name, out JsonElement element) && element.ValueKind == JsonValueKind.Number
          && element.TryGetInt32(out int value)
          ? value
          : null;

  private static List<string> OptStringArray(Dictionary<string, JsonElement> args, string name)
  {
    if (!args.TryGetValue(name, out JsonElement element))
    {
      return [];
    }

    if (element.ValueKind != JsonValueKind.Array)
    {
      throw new StateInputException($"'{name}' must be an array of strings.");
    }

    List<string> items = [];
    foreach (JsonElement item in element.EnumerateArray())
    {
      if (item.ValueKind != JsonValueKind.String)
      {
        throw new StateInputException($"'{name}' must contain only strings.");
      }

      items.Add(item.GetString()!);
    }
    return items;
  }
}

/// <summary>Signals malformed capability arguments during parsing. Public only because
///     CA1064 forbids non-public exception types; it never escapes the provider -
///     every action catches it and renders the message as a typed tool error.</summary>
public sealed class StateInputException : Exception
{
  public StateInputException() : base("Invalid capability arguments.") { }
  public StateInputException(string message) : base(message) { }
  public StateInputException(string message, Exception innerException) : base(message, innerException) { }
}
