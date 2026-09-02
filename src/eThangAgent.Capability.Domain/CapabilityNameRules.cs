using System.Text.RegularExpressions;

namespace eThangAgent.CapabilityDomain;

public static partial class CapabilityNameRules
{
  /// <summary>Action names are strings end-to-end: dispatch is a string switch,
  ///     receipts and grant sets are JSON keys. Named decision (W4): interior hyphens
  ///     are VALID — the source spec's broadcast vocabulary (agent.notify-subtree /
  ///     agent.notify-ancestors) requires them, and nothing generates C# identifiers
  ///     from action names (the old rule's stated reason, verified absent in src).
  ///     Dots, spaces, non-ASCII, and leading/trailing hyphens stay rejected.</summary>
  [GeneratedRegex("^[A-Za-z0-9_]+(-[A-Za-z0-9_]+)*$", RegexOptions.Compiled)]
  private static partial Regex Pattern();

  public static bool IsValidActionName(string name)
      => !string.IsNullOrWhiteSpace(name) && Pattern().IsMatch(name);
}
