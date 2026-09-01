using eThangAgent.ToolDomain;

namespace eThangAgent.AgentDomain;

/// <summary>A tool registry narrowed to an effective grant set (R1.1): Definitions is
///     filtered, Find returns null for names outside the set. The loop's generic
///     null-tool path would render UnknownTool — indistinguishable from a typo — so
///     dispatch consults <see cref="ExplainsRefusal"/> when Find misses: a denied-but-real
///     name yields the structured GrantViolation contract (R1.3), never UnknownTool.
///     Every refusal fires the audit callback (R1.4) — audit is a record of decisions,
///     never a state source (P2). Names are exact tool action ids, ordinal.</summary>
/// <summary>Wraps <paramref name="inner"/> exposing only tools named in
///     <paramref name="effective"/>. <paramref name="onDenial"/>, when supplied, fires
///     once per denied lookup with the denied tool's name. The set is consumed as
///     immutable; the spawner builds a fresh registry per child run.</summary>
public sealed class FilteredToolRegistry(
    IToolRegistry inner,
    IReadOnlySet<string> effective,
    Action<string>? onDenial = null) : IToolRegistry
{
  private readonly IToolRegistry _inner = inner ?? throw new ArgumentNullException(nameof(inner));
  private readonly IReadOnlySet<string> _effective = effective ?? throw new ArgumentNullException(nameof(effective));
  private readonly Action<string>? _onDenial = onDenial;

  public ITool? Find(string name)
  {
    ArgumentNullException.ThrowIfNull(name);
    if (_effective.Contains(name))
    {
      return _inner.Find(name);
    }

    ITool? existsUngranted = _inner.Find(name);
    if (existsUngranted is not null)
    {
      _onDenial?.Invoke(name);
    }

    return null;
  }

  public IReadOnlyList<ToolDefinition> Definitions
      => [.. _inner.Definitions.Where(t => _effective.Contains(t.Name))];

  /// <summary>The structured refusal for a denied dispatch (R1.3): the exact contract
  ///     line, or null when the name is simply unknown (the loop's standard UnknownTool
  ///     path — policy must never mask typo).</summary>
  public string? ExplainsRefusal(string name)
      => !_effective.Contains(name) && _inner.Find(name) is not null
          ? GrantViolation.For(name)
          : null;
}
