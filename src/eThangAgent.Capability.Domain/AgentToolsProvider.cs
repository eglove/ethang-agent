using eThangAgent.ToolDomain;

namespace eThangAgent.CapabilityDomain;

/// <summary>Exposes existing ITool instances as capability actions. Read's behavior,
///     format contract, and tests are unchanged — this is a pure adapter.</summary>
public sealed class AgentToolsProvider : ICapabilityProvider
{
    private readonly Dictionary<string, ITool> _tools;

    public AgentToolsProvider(string id, IReadOnlyList<AgentToolBinding> bindings)
    {
        Id = id ?? throw new ArgumentNullException(nameof(id));
        bindings = bindings ?? throw new ArgumentNullException(nameof(bindings));
        _tools = bindings.ToDictionary(b => b.Tool.Definition.Name, b => b.Tool, StringComparer.Ordinal);
        Actions = bindings.Select(b => new ActionDescriptor(
            b.Tool.Definition.Name,
            b.Summary,
            b.Tool.Definition.Description,
            b.Tool.Definition.Parameters
                .Select(p => new ActionParameter(p.Name, p.Type.ToString(), p.Description))
                .ToList(),
            b.Tool.Definition.RequiredParameters)).ToList();
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
