using eThangAgent.ToolDomain;

namespace eThangAgent.PowerShell.ACL;

/// <summary>
///     Bridges in-script tool calls back into the IToolRegistry. Blocking on the async
///     tool call is safe: the runspace pipeline thread has no synchronization context.
/// </summary>
public sealed class ToolBroker
{
    private readonly IToolRegistry _registry;

    public ToolBroker(IToolRegistry registry)
        => _registry = registry ?? throw new ArgumentNullException(nameof(registry));

    public IReadOnlyList<ToolDefinition> WrappableDefinitions
        => _registry.Definitions.Where(d => d.Name != ExecTool.ToolName).ToList();

    public string InvokeTool(string name, object? input)
    {
        var tool = _registry.Find(name);
        if (tool is null)
            throw new ExecToolCallException($"Error [UnknownTool]: Unknown tool: {name}.");
        if (input is null)
            throw new ExecToolCallException(
                "Error [InvalidToolInput]: Pass a hashtable of tool arguments, e.g. " +
                "read @{ path = 'file.txt'; startLine = 1; endLine = 5 }.");

        string json;
        try
        {
            json = PowerShellValueConverter.ToJson(input);
        }
        catch (ExecInputConversionException ex)
        {
            throw new ExecToolCallException($"Error [InvalidToolInput]: {ex.Message}");
        }

        var result = tool.ExecuteAsync(new RawToolInput(name, json)).GetAwaiter().GetResult();
        if (result.IsError)
            throw new ExecToolCallException(result.Content);
        return result.Content;
    }

    /// <summary>One line per tool: name(parameters): description. Returns a string so
    ///     the engine's output renderer passes it through verbatim.</summary>
    public string DescribeTools()
        => string.Join("\n", WrappableDefinitions.Select(d =>
            $"{d.Name}({string.Join(", ", d.Parameters.Select(p => $"{p.Name}: {p.Type}"))}): {d.Description}"));
}

public sealed class ExecToolCallException : Exception
{
    public ExecToolCallException(string message) : base(message) { }
}
