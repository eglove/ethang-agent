using System.Text.RegularExpressions;

namespace eThangAgent.CapabilityDomain;

public static partial class CapabilityNameRules
{
  /// <summary>Action names become C# method names — restrict to what is
  ///     safe to generate, reject rather than sanitize.</summary>
  [GeneratedRegex("^[A-Za-z0-9_]+$", RegexOptions.Compiled)]
  private static partial Regex Pattern();

  public static bool IsValidActionName(string name)
      => !string.IsNullOrWhiteSpace(name) && Pattern().IsMatch(name);
}
