using System.Text.Json;
using eThangAgent.CapabilityDomain;
using eThangAgent.SharedKernel;

namespace eThangAgent.StateDomain;

public sealed class StateCapabilityProvider : ICapabilityProvider
{
    public const string ProviderId = "state";

    /// <summary>Namespace owned by the todo tool's list storage. The reservation is
    ///     enforced HERE, at the model-facing capability boundary: a 'foreign write'
    ///     is a model-invoked state.set/state.delete, so only those are gated, while
    ///     internal composition (the todo tool's own adapter over IStateService)
    ///     flows unrestricted.</summary>
    private const string ReservedTodoNamespace = "todo";

    private readonly IStateService _service;

    public StateCapabilityProvider(IStateService service)
        => _service = service ?? throw new ArgumentNullException(nameof(service));

    public string Id => ProviderId;

    public IReadOnlyList<ActionDescriptor> Actions { get; } =
    [
        new("get", "Read a durable state value, or a line range of it.",
            "Reads one namespaced key. Fails with KeyNotFound when absent. Optional startLine/endLine return only that range under an envelope '[<key> v<version> | lines <S>-<E> of <T>]'; an endLine past the last line is clamped with a visible '[note] ... clamped.' warning.",
            [new ActionParameter("key", "String", "Namespaced key, e.g. current/head."),
             new ActionParameter("startLine", "Integer", "Optional, only together with endLine. First line to read, >= 1."),
             new ActionParameter("endLine", "Integer", "Optional, only together with startLine. Last line to read.")]),
        new("set", "Write a durable state value with optional compare-and-swap.",
            "Creates or updates a key. Supply expectedVersion to require the current version; a mismatch fails closed with VersionConflict naming the current version. Returns the new version. Namespace 'todo' is reserved and fails with ReservedNamespace.",
            [new ActionParameter("key", "String", "Namespaced key."),
             new ActionParameter("value", "String", "Value to store."),
             new ActionParameter("expectedVersion", "Integer", "Optional. Fail unless the stored version matches.")]),
        new("delete", "Delete a durable state key.",
            "Removes a key. Supply expectedVersion for a compare-and-swap delete. Namespace 'todo' is reserved and fails with ReservedNamespace.",
            [new ActionParameter("key", "String", "Namespaced key."),
             new ActionParameter("expectedVersion", "Integer", "Optional. Fail unless the stored version matches.")]),
        new("list", "List state keys.",
            "Lists keys as 'ns/name v<version>' lines, optionally filtered by namespace.",
            [new ActionParameter("ns", "String", "Optional namespace filter.")]),
        new("find", "Full-text search over workspace state values and key names.",
            "Searches all state in this workspace with SQLite FTS5 over values, namespaces, and key names. Output contract: header line '[state.find '<query>'] <N> hit(s)', then per hit the key as ns/name and an indented snippet line. Zero hits prints only the header. Malformed queries fail with InvalidQuery rather than returning empty.",
            [new ActionParameter("query", "String", "Required. FTS5 query text (supports prefix*, AND/OR/NOT)."),
             new ActionParameter("limit", "Integer", "Optional. Max hits, 1..100, default 20.")]),
        new("append", "Append one line to a state value atomically.",
            "CAS append: the line is added to the key's stored value with a newline separator. A missing key is created holding just the line. Text must be a single line without leading or trailing whitespace (InvalidText otherwise). Fails closed with VersionConflict when expectedVersion does not match — re-get, reconcile, retry; never blind-overwrite. This is how SDD ledgers are maintained.",
            [new ActionParameter("key", "String", "Namespaced key, e.g. sdd.my-plan/ledger."),
             new ActionParameter("text", "String", "The single line to append."),
             new ActionParameter("expectedVersion", "Integer", "Optional. Fail unless the stored version matches.")]),
        new("prune", "Bulk-delete every state key under a namespace prefix.",
            "Deletes all keys whose namespace equals the prefix or starts with '<prefix>.'. The dotted boundary is respected: prefix 'sdd.alpha' does not touch 'sdd.alphabeta'. Reserved namespaces ('todo', 'current') are rejected. Returns '[prune <prefix>] <N> key(s) removed'. Intended for cleaning up SDD task briefs and reports after a plan finishes.",
            [new ActionParameter("prefix", "String", "Namespace prefix, e.g. sdd.my-plan.")]),
        new("transition", "Attach a claim with evidence (stored, never run on attach).",
            "Records a labeled move from one world-state to another with summary and evidence commands. Evidence is replayable but has NOT run. Returns the transition id; status starts pending.",
            [new ActionParameter("from", "String", "Prior state label."),
             new ActionParameter("to", "String", "New state label."),
             new ActionParameter("summary", "String", "What this claim asserts."),
             new ActionParameter("evidence", "String[]", "PowerShell commands that, when run, should confirm the claim.")]),
        new("verify", "Run attached evidence fail-closed and certify.",
            "Runs the evidence for the selected transitions (default: all pending) fail-closed. Certifies only when every command confirms; otherwise reports violated with blocking reasons and revokes any head certificate first.",
            [new ActionParameter("ids", "String[]", "Optional transition ids; default all pending.")]),
        new("checkgoal", "Run the goal/check commands and report.",
            "Runs the commands stored at goal/check and reports pass/fail. Report-only — no certification.",
            []),
        new("history", "Replay the state timeline.",
            "Returns the most recent timeline events (transitions, certified, violated).",
            [new ActionParameter("limit", "Integer", "Optional. Default 20.")]),
    ];

    public async Task<CapabilityInvocationResult> InvokeAsync(
        string actionName, string jsonArguments, CancellationToken ct = default)
    {
        try
        {
            return actionName switch
            {
                "get" => await GetAsync(jsonArguments),
                "find" => await SearchAsync(jsonArguments),
                "append" => await AppendAsync(jsonArguments),
                "prune" => await PruneAsync(jsonArguments),
                "set" => await SetAsync(jsonArguments),
                "delete" => await DeleteAsync(jsonArguments),
                "list" => await ListAsync(jsonArguments),
                "transition" => await TransitionAsync(jsonArguments),
                "verify" => await VerifyAsync(jsonArguments),
                "checkgoal" => await CheckGoalAsync(),
                "history" => await HistoryAsync(jsonArguments),
                _ => CapabilityInvocationResult.Fail($"Error [UnknownAction]: Unknown action: {actionName}."),
            };
        }
        catch (StateInputException ex)
        {
            return CapabilityInvocationResult.Fail($"Error [InvalidActionInput]: {ex.Message}");
        }
    }

    private async Task<CapabilityInvocationResult> GetAsync(string json)
    {
        var args = ParseArgs(json, Allowed("key", "startLine", "endLine"));
        var key = ReqString(args, "key");
        if (!args.ContainsKey("startLine") && !args.ContainsKey("endLine"))
            return ToResult(await _service.GetAsync(key));

        // Strict range validation: both-or-neither, start >= 1, end >= start.
        if (!args.TryGetValue("startLine", out var sEl) || sEl.ValueKind != JsonValueKind.Number || !sEl.TryGetInt32(out var start))
            throw new StateInputException("'startLine' is required together with 'endLine' and must be an integer.");
        if (!args.TryGetValue("endLine", out var eEl) || eEl.ValueKind != JsonValueKind.Number || !eEl.TryGetInt32(out var end))
            throw new StateInputException("'endLine' is required together with 'startLine' and must be an integer.");
        if (start < 1)
            throw new StateInputException("'startLine' must be >= 1.");
        if (end < start)
            throw new StateInputException("'endLine' must be >= 'startLine'.");

        var value = await _service.GetAsync(key);
        if (!value.IsSuccess) return Gutter(value.Error!);
        var lines = value.Value!.Split('\n');
        var total = lines.Length;
        var clampedEnd = Math.Min(end, total);
        var slice = string.Join("\n", lines[(start - 1)..clampedEnd]);

        // Version comes from ListAsync(ns) so IStateService signatures stay untouched.
        string? versionPart = null;
        var slashAt = key.IndexOf('/');
        var keys = await _service.ListAsync(key[..slashAt]);
        if (keys.IsSuccess)
        {
            var prefix = key + " v";
            var match = keys.Value!.FirstOrDefault(k => k.StartsWith(prefix, StringComparison.Ordinal));
            if (match is not null)
                versionPart = "v" + match[prefix.Length..];
        }

        var header = versionPart is null
            ? $"[{key} | lines {start}-{clampedEnd} of {total}]"
            : $"[{key} {versionPart} | lines {start}-{clampedEnd} of {total}]";
        var output = header + "\n" + slice;
        if (end > total)
            output += $"\n[note] endLine {end} exceeds last line {total}; clamped.";
        return CapabilityInvocationResult.Ok(output);
    }

    private async Task<CapabilityInvocationResult> SearchAsync(string json)
    {
        var args = ParseArgs(json, Allowed("query", "limit"));
        var query = ReqString(args, "query");
        var limit = OptInt(args, "limit") ?? 20;
        var result = await _service.SearchAsync(query, limit);
        if (!result.IsSuccess) return Gutter(result.Error!);
        var sb = new System.Text.StringBuilder();
        sb.Append($"[state.find '{query}'] {result.Value!.Count} hit(s)");
        foreach (var hit in result.Value!)
        {
            sb.Append($"\n{hit.Ns}/{hit.Name}");
            sb.Append($"\n  {hit.Snippet}");
        }
        return CapabilityInvocationResult.Ok(sb.ToString());
    }

    private async Task<CapabilityInvocationResult> SetAsync(string json)
    {
        var args = ParseArgs(json, Allowed("key", "value", "expectedVersion"));
        if (ReservedNamespaceError(ReqString(args, "key")) is { } setError)
            return Gutter(setError);
        var saved = await _service.SetAsync(ReqString(args, "key"), ReqString(args, "value"), OptInt(args, "expectedVersion"));
        return saved.IsSuccess
            ? CapabilityInvocationResult.Ok($"saved {saved.Value!.Ns}/{saved.Value.Name} v{saved.Value.Version}")
            : Gutter(saved.Error!);
    }

    private async Task<CapabilityInvocationResult> DeleteAsync(string json)
    {
        var args = ParseArgs(json, Allowed("key", "expectedVersion"));
        var key = ReqString(args, "key");
        if (ReservedNamespaceError(key) is { } deleteError)
            return Gutter(deleteError);
        return ToResult(await _service.DeleteAsync(key, OptInt(args, "expectedVersion")));
    }

    /// <summary>Parses the key's namespace exactly as the service will (StateKey.Parse)
    ///     and returns the ReservedNamespace error when it names the todo tool's
    ///     namespace; null when the write may proceed to the service.</summary>
    private static Error? ReservedNamespaceError(string key)
    {
        var parsed = StateKey.Parse(key);
        return parsed.IsSuccess && parsed.Value.Ns == ReservedTodoNamespace
            ? new Error("ReservedNamespace",
                $"'{key}' uses reserved namespace '{ReservedTodoNamespace}', which is owned by " +
                "the todo tool. Choose a different namespace.")
            : null;
    }

    private async Task<CapabilityInvocationResult> ListAsync(string json)
        => ToResult(await _service.ListAsync(OptString(ParseArgs(json, Allowed("ns")), "ns")));

    private async Task<CapabilityInvocationResult> TransitionAsync(string json)
    {
        var args = ParseArgs(json, Allowed("from", "to", "summary", "evidence"));
        return ToResult(await _service.TransitionAsync(
            ReqString(args, "from"), ReqString(args, "to"), ReqString(args, "summary"),
            OptStringArray(args, "evidence")));
    }

    private async Task<CapabilityInvocationResult> VerifyAsync(string json)
    {
        var report = await _service.VerifyAsync(OptStringArray(ParseArgs(json, Allowed("ids")), "ids"));
        return Report(report);
    }

    private async Task<CapabilityInvocationResult> CheckGoalAsync()
        => Report(await _service.CheckGoalAsync());

    private async Task<CapabilityInvocationResult> HistoryAsync(string json)
        => ToResult(await _service.HistoryAsync(OptInt(ParseArgs(json, Allowed("limit")), "limit") ?? 20));

    private async Task<CapabilityInvocationResult> AppendAsync(string json)
    {
        var args = ParseArgs(json, Allowed("key", "text", "expectedVersion"));
        var saved = await _service.AppendAsync(ReqString(args, "key"), ReqString(args, "text"), OptInt(args, "expectedVersion"));
        return saved.IsSuccess
            ? CapabilityInvocationResult.Ok($"appended to {saved.Value!.Ns}/{saved.Value.Name} v{saved.Value.Version}")
            : Gutter(saved.Error!);
    }

    private async Task<CapabilityInvocationResult> PruneAsync(string json)
    {
        var args = ParseArgs(json, Allowed("prefix"));
        var result = await _service.DeletePrefixAsync(ReqString(args, "prefix"));
        if (!result.IsSuccess) return Gutter(result.Error!);
        return CapabilityInvocationResult.Ok($"[prune {ReqString(args, "prefix")}] {result.Value} key(s) removed");
    }

    private static CapabilityInvocationResult ToResult<T>(Result<T> result)
        => result.IsSuccess
            ? CapabilityInvocationResult.Ok(result.Value!.ToString() ?? "")
            : Gutter(result.Error!);

    private static CapabilityInvocationResult Gutter(Error error)
        => CapabilityInvocationResult.Fail($"Error [{error.Code}]: {error.Message}");

    private static CapabilityInvocationResult Report(CertificationReport report)
        => CapabilityInvocationResult.Ok(JsonSerializer.Serialize(report));

    private static IReadOnlySet<string> Allowed(params string[] names) => new HashSet<string>(names, StringComparer.Ordinal);

    private static Dictionary<string, JsonElement> ParseArgs(string json, IReadOnlySet<string> allowed)
    {
        JsonElement root;
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json);
            root = doc.RootElement.Clone();
        }
        catch (JsonException ex)
        {
            throw new StateInputException($"Arguments are not valid JSON: {ex.Message}");
        }
        if (root.ValueKind != JsonValueKind.Object)
            throw new StateInputException("Arguments must be a JSON object.");
        var args = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var property in root.EnumerateObject())
        {
            if (!allowed.Contains(property.Name))
                throw new StateInputException($"Unknown parameter '{property.Name}'.");
            args[property.Name] = property.Value.Clone();
        }
        return args;
    }

    private static string ReqString(Dictionary<string, JsonElement> args, string name)
    {
        if (!args.TryGetValue(name, out var element)
            || element.ValueKind != JsonValueKind.String
            || string.IsNullOrEmpty(element.GetString()))
            throw new StateInputException($"'{name}' is required and must be a non-empty string.");
        return element.GetString()!;
    }

    private static string? OptString(Dictionary<string, JsonElement> args, string name)
        => args.TryGetValue(name, out var element) && element.ValueKind == JsonValueKind.String
            ? element.GetString()
            : null;

    private static int? OptInt(Dictionary<string, JsonElement> args, string name)
        => args.TryGetValue(name, out var element) && element.ValueKind == JsonValueKind.Number
            && element.TryGetInt32(out var value)
            ? value
            : null;

    private static IReadOnlyList<string> OptStringArray(Dictionary<string, JsonElement> args, string name)
    {
        if (!args.TryGetValue(name, out var element))
            return [];
        if (element.ValueKind != JsonValueKind.Array)
            throw new StateInputException($"'{name}' must be an array of strings.");
        var items = new List<string>();
        foreach (var item in element.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String)
                throw new StateInputException($"'{name}' must contain only strings.");
            items.Add(item.GetString()!);
        }
        return items;
    }

    private sealed class StateInputException : Exception
    {
        public StateInputException(string message) : base(message) { }
    }
}
