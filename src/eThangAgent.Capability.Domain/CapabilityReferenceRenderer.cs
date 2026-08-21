using System.Text;

namespace eThangAgent.CapabilityDomain;

public static class CapabilityReferenceRenderer
{
    public static string Render(ICapabilityRegistry registry)
    {
        var sb = new StringBuilder("## Available actions");
        foreach (var provider in registry.Providers)
        {
            sb.Append("\n").Append($"{provider.Id}:");
            foreach (var action in provider.Actions)
            {
                var parameters = string.Join(", ",
                    action.Parameters.Select(p => $"{p.Name}: {p.Type}"));
                sb.Append("\n").Append($"{action.Name}({parameters}): {action.Summary}");
            }
        }
        return sb.ToString();
    }
}
