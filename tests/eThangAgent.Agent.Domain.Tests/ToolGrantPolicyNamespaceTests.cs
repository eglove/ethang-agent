namespace eThangAgent.AgentDomain.Tests;

/// <summary>Namespace-aware MCP grant semantics (issue #108): a trailing .* entry is
///     a namespace pattern; the namespace root (mcp) rides along so the dispatch tool
///     resolves; bare mcp inherits the parent's namespace reach; widening is measured
///     over namespace reach, never over entry strings. Exact ids are unchanged.</summary>
public class ToolGrantPolicyNamespaceTests
{
  private static readonly IReadOnlySet<string> Parent = new HashSet<string>(StringComparer.Ordinal)
    { "agent.spawn", "web_fetch", "exec", "mcp" };

  private static readonly IReadOnlySet<string> ScopedParent = new HashSet<string>(StringComparer.Ordinal)
    { "mcp", "mcp.github.*" };

  [Fact]
  public void PatternGrant_AddsPatternAndRoot_ToEffectiveSet()
  {
    ToolGrantPolicy policy = new(new Dictionary<string, string>
    {
      [ToolGrantPolicy.AllowKey] = "mcp.github.*",
    });

    IReadOnlySet<string> effective = policy.EffectiveTools(Parent);
    Assert.Contains("mcp", effective);
    Assert.Contains("mcp.github.*", effective);
  }

  [Fact]
  public void PatternGrant_WithinParentReach_IsNotWidening()
  {
    ToolGrantPolicy policy = new(new Dictionary<string, string>
    {
      [ToolGrantPolicy.AllowKey] = "mcp.github.*",
    });

    Assert.Empty(policy.WideningViolations(Parent));
  }

  [Fact]
  public void PatternGrant_OutsideParentReach_IsWidening()
  {
    ToolGrantPolicy policy = new(new Dictionary<string, string>
    {
      [ToolGrantPolicy.AllowKey] = "mcp.gitlab.*",
    });

    string violation = Assert.Single(policy.WideningViolations(ScopedParent));
    Assert.Contains("mcp.gitlab.*", violation, StringComparison.Ordinal);
  }

  [Fact]
  public void PatternGrant_WithNoParentNamespaceReach_IsWidening()
  {
    // A parent surface without any mcp entry cannot delegate MCP reach at all.
    IReadOnlySet<string> bare = new HashSet<string>(StringComparer.Ordinal) { "read", "exec" };
    ToolGrantPolicy policy = new(new Dictionary<string, string>
    {
      [ToolGrantPolicy.AllowKey] = "mcp.github.*",
    });

    Assert.NotEmpty(policy.WideningViolations(bare));
  }

  [Fact]
  public void BareMcpGrant_InheritsParentPatternReach()
  {
    ToolGrantPolicy policy = new(new Dictionary<string, string>
    {
      [ToolGrantPolicy.AllowKey] = "mcp",
    });

    IReadOnlySet<string> effective = policy.EffectiveTools(ScopedParent);
    Assert.Contains("mcp", effective);
    Assert.Contains("mcp.github.*", effective);
  }

  [Fact]
  public void BareMcpGrant_FromFullReachParent_IsNotWidening()
  {
    ToolGrantPolicy policy = new(new Dictionary<string, string>
    {
      [ToolGrantPolicy.AllowKey] = "mcp",
    });

    Assert.Empty(policy.WideningViolations(Parent));
  }

  [Fact]
  public void BareMcpGrant_FromNoReachParent_IsWidening()
  {
    IReadOnlySet<string> bare = new HashSet<string>(StringComparer.Ordinal) { "read", "exec" };
    ToolGrantPolicy policy = new(new Dictionary<string, string>
    {
      [ToolGrantPolicy.AllowKey] = "mcp",
    });

    _ = Assert.Single(policy.WideningViolations(bare));
  }

  [Fact]
  public void NestedPatternGrant_WithinParentPattern_IsNotWidening()
  {
    ToolGrantPolicy policy = new(new Dictionary<string, string>
    {
      [ToolGrantPolicy.AllowKey] = "mcp.github.private.*",
    });

    Assert.Empty(policy.WideningViolations(ScopedParent));
    IReadOnlySet<string> effective = policy.EffectiveTools(ScopedParent);
    Assert.Contains("mcp.github.private.*", effective);
  }

  [Fact]
  public void BroaderPatternGrant_OverParentPattern_IsWidening()
  {
    ToolGrantPolicy policy = new(new Dictionary<string, string>
    {
      [ToolGrantPolicy.AllowKey] = "mcp.*",
    });

    Assert.NotEmpty(policy.WideningViolations(ScopedParent));
  }

  [Fact]
  public void DenyPattern_RemovesCoveredPatternAndReach()
  {
    ToolGrantPolicy policy = new(new Dictionary<string, string>
    {
      [ToolGrantPolicy.AllowKey] = "mcp.*",
      [ToolGrantPolicy.DenyKey] = "mcp.github.*",
    });

    // The deny does not subtract within the allow pattern (unexpressible in the
    // additive entry model) - the combination is a widening-style violation, named.
    Assert.NotEmpty(policy.WideningViolations(Parent));
  }

  [Fact]
  public void ExactIdGrants_AreUnchanged_ForNonNamespaceTools()
  {
    ToolGrantPolicy policy = new(new Dictionary<string, string>
    {
      [ToolGrantPolicy.AllowKey] = "web_fetch",
    });

    IReadOnlySet<string> effective = policy.EffectiveTools(Parent);
    Assert.Equal(new HashSet<string>(StringComparer.Ordinal) { "web_fetch" }, effective);
  }
}
