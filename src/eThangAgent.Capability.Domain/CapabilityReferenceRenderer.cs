using System.Globalization;
using System.Text;

namespace eThangAgent.CapabilityDomain;

public static class CapabilityReferenceRenderer
{
  public static string Render(ICapabilityRegistry registry)
  {
    ArgumentNullException.ThrowIfNull(registry);
    StringBuilder sb = new("## Available actions");
    foreach (ProviderCapabilities provider in registry.Providers)
    {
      _ = sb.Append('\n').Append(CultureInfo.InvariantCulture, $"{provider.Id}:");
      foreach (ActionDescriptor action in provider.Actions)
      {
        string parameters = string.Join(", ",
            action.Parameters.Select(p => $"{p.Name}: {p.Type}"));
        _ = sb.Append('\n').Append(CultureInfo.InvariantCulture, $"{action.Name}({parameters}): {action.Summary}");
      }
    }
    return sb.ToString();
  }
}
