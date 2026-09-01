namespace eThangAgent.AgentDomain;

/// <summary>Spawn-time capability grants (D9): the child's effective tool set is the
///     intersection of its parent's effective set with the allow list, minus the deny list.
///     Privilege cannot grow down the tree — a child grant that widens beyond the parent's
///     effective set is a spawn-validation error, never a clamp (A5). Null grants mean the
///     today's-child default surface (the registry's own child set).</summary>
public sealed class ToolGrantPolicy
{
  public const string AllowKey = "tool.allow";
  public const string DenyKey = "tool.deny";

  private readonly IReadOnlyDictionary<string, string> _grants = new Dictionary<string, string>();

  public ToolGrantPolicy(IReadOnlyDictionary<string, string>? grants)
      => _grants = grants ?? _grants;

  /// <summary>Whether any grant is present (absent grants keep the default child surface).</summary>
  public bool HasGrants => _grants.ContainsKey(AllowKey) || _grants.ContainsKey(DenyKey);

  /// <summary>Applies allow/deny to the parent's effective set. allow absent = inherit all.
  ///     deny absent = remove nothing. Names are exact tool action ids, semicolon-separated.</summary>
  public IReadOnlySet<string> EffectiveTools(IReadOnlySet<string> parentEffective)
  {
    HashSet<string> effective = [.. parentEffective];
    if (_grants.TryGetValue(AllowKey, out string? allow))
    {
      effective.IntersectWith(ParseList(allow));
    }

    if (_grants.TryGetValue(DenyKey, out string? deny))
    {
      effective.ExceptWith(ParseList(deny));
    }

    return effective;
  }

  /// <summary>Spawn validation (D9): every allowed tool must already be in the parent's
  ///     effective set. Returns the offending names; empty = valid.</summary>
  public IReadOnlyList<string> WideningViolations(IReadOnlySet<string> parentEffective)
  {
    return !_grants.TryGetValue(AllowKey, out string? allow)
        ? []
        : [.. ParseList(allow).Where(name => !parentEffective.Contains(name))];
  }

  private static IEnumerable<string> ParseList(string semicolonSeparated)
      => semicolonSeparated
          .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
          .Where(entry => entry.Length > 0);
}
