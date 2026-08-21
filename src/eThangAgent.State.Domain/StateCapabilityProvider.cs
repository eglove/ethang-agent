using System.Text.Json;
using eThangAgent.CapabilityDomain;
using eThangAgent.SharedKernel;

namespace eThangAgent.StateDomain;

public sealed class StateCapabilityProvider : ICapabilityProvider
{
    public const string ProviderId = "state";

    private readonly IStateService _service;

    public StateCapabilityProvider(IStateService service)
        => _service = service ?? throw new ArgumentNullException(nameof(service));

    public string Id => ProviderId;

    public IReadOnlyList<ActionDescriptor> Actions { get; } =
    [
        new("get", "Read a durable state value.",
            "Reads one namespaced key. Fails with KeyNotFound when absent.",
            [new ActionParameter("key", "String", "Namespaced key, e.g. current/head.")]),
        new("set", "Write a durable state value with optional compare-and-swap.",
            "Creates or updates a key. Supply expectedVersion to require the current version; a mismatch fails closed with VersionConflict naming the current version. Returns the new version.",
            [new ActionParameter("key", "String", "Namespaced key."),
             new ActionParameter("value", "String", "Value to store."),
             new ActionParameter("expectedVersion", "Integer", "Optional. Fail unless the stored version matches.")]),
        new("delete", "Delete a durable state key.",
            "Removes a key. Supply expectedVersion for a compare-and-swap delete.",
            [new ActionParameter("key", "String", "Namespaced key."),
             new ActionParameter("expectedVersion", "Integer", "Optional. Fail unless the stored version matches.")]),
        new("list", "List state keys.",
            "Lists keys as 'ns/name v<version>' lines, optionally filtered by namespace.",
            [new ActionParameter("ns", "String", "Optional namespace filter.")]),
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
        var args = ParseArgs(json, Allowed("key"));
        return ToResult(await _service.GetAsync(ReqString(args, "key")));
    }

    private async Task<CapabilityInvocationResult> SetAsync(string json)
    {
        var args = ParseArgs(json, Allowed("key", "value", "expectedVersion"));
        var saved = await _service.SetAsync(ReqString(args, "key"), ReqString(args, "value"), OptInt(args, "expectedVersion"));
        return saved.IsSuccess
            ? CapabilityInvocationResult.Ok($"saved {saved.Value!.Ns}/{saved.Value.Name} v{saved.Value.Version}")
            : Gutter(saved.Error!);
    }

    private async Task<CapabilityInvocationResult> DeleteAsync(string json)
        => ToResult(await _service.DeleteAsync(
            ReqString(ParseArgs(json, Allowed("key", "expectedVersion")), "key"),
            OptInt(ParseArgs(json, Allowed("key", "expectedVersion")), "expectedVersion")));

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
