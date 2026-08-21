namespace eThangAgent.ToolDomain;

public sealed class ToolRegistry : IToolRegistry
{
    private readonly Dictionary<string, ITool> _tools;

    public ToolRegistry(IEnumerable<ITool> tools)
    {
        _tools = tools.ToDictionary(t => t.Definition.Name, StringComparer.Ordinal);
    }

    public ITool? Find(string name)
        => _tools.TryGetValue(name, out var tool) ? tool : null;

    public IReadOnlyList<ToolDefinition> Definitions
        => _tools.Values.Select(t => t.Definition).ToList();
}
