using System.Text;
using eThangAgent.CapabilityDomain;
using eThangAgent.ToolDomain;

namespace eThangAgent.PowerShell.ACL;

/// <summary>Bridges in-script action calls into the ICapabilityRegistry. Blocking on the
///     async invocation is safe: the runspace pipeline thread has no synchronization context.</summary>
public sealed class ToolBroker
{
    private readonly ICapabilityRegistry _registry;

    public ToolBroker(ICapabilityRegistry registry)
        => _registry = registry ?? throw new ArgumentNullException(nameof(registry));

    public string InvokeTool(string nameOrRef, object? input)
    {
        var resolved = _registry.Resolve(nameOrRef);
        if (!resolved.IsSuccess)
            throw new ExecToolCallException($"Error [UnknownAction]: {resolved.Error!.Message}");
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

        var result = _registry.InvokeAsync(resolved.Value!, json).GetAwaiter().GetResult();
        if (result.IsError)
            throw new ExecToolCallException(result.Content);
        return result.Content;
    }

    public string ListActions()
        => string.Join("\n", _registry.Providers.SelectMany(p => p.Actions)
            .Select(a => $"{a.Name}({string.Join(", ", a.Parameters.Select(p => $"{p.Name}: {p.Type}"))})"));

    public string DescribeAction(string nameOrRef)
    {
        var resolved = _registry.Resolve(nameOrRef);
        if (!resolved.IsSuccess)
            throw new ExecToolCallException($"Error [UnknownAction]: {resolved.Error!.Message}");
        var action = resolved.Value!.Action;
        var sb = new StringBuilder($"{action.Name} — {action.Summary}\n\n{action.Description}");
        foreach (var parameter in action.Parameters)
            sb.Append($"\n- {parameter.Name}: {parameter.Type} — {parameter.Description}");
        return sb.ToString();
    }

    public string ListProviders()
        => string.Join("\n", _registry.Providers.Select(p => $"{p.Id} ({p.Actions.Count} actions)"));
}

public sealed class ExecToolCallException : Exception
{
    public ExecToolCallException(string message) : base(message) { }
}
