using eThangAgent.ToolDomain;

namespace eThangAgent.CapabilityDomain;

/// <summary>Exposes existing ITool instances as capability actions. Read's behavior,
///     format contract, and tests are unchanged — this is a pure adapter.
///     Every action is <see cref="TimeoutPolicy.SelfManaged"/>: ITool contracts parse
///     their own timeoutSeconds envelope (ToolCallEnvelopeParser) and bound themselves
///     via ToolExecution — clarify deliberately runs unbounded while still validating.</summary>
public sealed class AgentToolsProvider : ICapabilityProvider
{
    private readonly Dictionary<string, ITool> _tools;
    private readonly IReadOnlyList<AgentToolBinding> _bindings;

    public AgentToolsProvider(string id, IReadOnlyList<AgentToolBinding> bindings)
    {
        Id = id ?? throw new ArgumentNullException(nameof(id));
        bindings = bindings ?? throw new ArgumentNullException(nameof(bindings));
        _bindings = bindings;
        _tools = bindings.ToDictionary(b => b.Tool.Definition.Name, b => b.Tool, StringComparer.Ordinal);
        Actions = bindings.Select(b => new ActionDescriptor(
            b.Tool.Definition.Name,
            b.Summary,
            b.Tool.Definition.Description,
            b.Tool.Definition.Parameters
                .Select(p => new ActionParameter(p.Name, p.Type.ToString(), p.Description))
                .ToList(),
            b.Tool.Definition.RequiredParameters,
            TimeoutPolicy.SelfManaged)).ToList();
    }

    /// <summary>A copy of this provider without the named actions — how the composition
    ///     root hides human-facing tools (clarify) from sub-agent surfaces. Unknown names
    ///     fail loudly: a renamed tool must never silently survive an exclusion filter.</summary>
    public AgentToolsProvider Except(params string[] actionNames)
    {
        var remove = actionNames.ToHashSet(StringComparer.Ordinal);
        var filtered = _bindings.Where(b => !remove.Contains(b.Tool.Definition.Name)).ToList();
        if (filtered.Count != _bindings.Count - remove.Count)
            throw new ArgumentException(
                "Except() named an action this provider does not expose: " +
                string.Join(", ", actionNames.Where(n => !_tools.ContainsKey(n))), nameof(actionNames));
        return new AgentToolsProvider(Id, filtered);
    }

    public string Id { get; }

    public IReadOnlyList<ActionDescriptor> Actions { get; }

    public async Task<CapabilityInvocationResult> InvokeAsync(
        string actionName, string jsonArguments, CancellationToken ct = default)
    {
        if (!_tools.TryGetValue(actionName, out var tool))
            return CapabilityInvocationResult.Fail($"Error [UnknownAction]: Unknown action: {actionName}.");

        var result = await tool.ExecuteAsync(new RawToolInput(actionName, jsonArguments), ct);
        return new CapabilityInvocationResult(result.Content, result.IsError);
    }
}
