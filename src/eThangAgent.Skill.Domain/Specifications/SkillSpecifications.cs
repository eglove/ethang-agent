using System.Text.RegularExpressions;

namespace eThangAgent.SkillDomain;

public static partial class SkillSpecifications
{
    // Lowercase alphanumeric + hyphens; never starts with a hyphen; ≤ 64 chars.
    [GeneratedRegex("^[a-z0-9][a-z0-9-]{0,63}$")]
    public static partial Regex ValidName { get; }
}
