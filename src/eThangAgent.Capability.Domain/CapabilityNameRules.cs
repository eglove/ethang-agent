using System.Text.RegularExpressions;

namespace eThangAgent.CapabilityDomain;

public static class CapabilityNameRules
{
    private static readonly Regex Pattern = new("^[A-Za-z0-9_]+$", RegexOptions.Compiled);

    /// <summary>Action names become PowerShell function names — restrict to what is
    ///     safe to generate, reject rather than sanitize.</summary>
    public static bool IsValidActionName(string name)
        => !string.IsNullOrWhiteSpace(name) && Pattern.IsMatch(name);
}
