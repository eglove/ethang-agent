namespace eThangAgent.AgentDomain.Tests;

/// <summary>Grant semantics: allow∩parent − deny, widening rejected, absent grants pass
///     the default surface through untouched (D9/A5).</summary>
public class ToolGrantPolicyTests
{
  private static readonly IReadOnlySet<string> Parent = new HashSet<string>(StringComparer.Ordinal)
    { "agent.spawn", "web_fetch", "exec", "read" };

  [Fact]
  public void NoGrants_HasGrantsFalse_AndEffectiveIsParentUnchanged()
  {
    ToolGrantPolicy policy = new(null);
    Assert.False(policy.HasGrants);
    Assert.Equal(Parent, policy.EffectiveTools(Parent));
    Assert.Empty(policy.WideningViolations(Parent));
  }

  [Fact]
  public void Allow_Narrows_ToTheIntersection()
  {
    ToolGrantPolicy policy = new(new Dictionary<string, string>
    {
      [ToolGrantPolicy.AllowKey] = "web_fetch; read; tool-not-in-parent",
    });

    IReadOnlySet<string> effective = policy.EffectiveTools(Parent);
    Assert.Equal(new HashSet<string>(StringComparer.Ordinal) { "web_fetch", "read" }, effective);
    _ = policy.WideningViolations(Parent);
    Assert.NotEmpty(policy.WideningViolations(Parent));
  }

  [Fact]
  public void Deny_RemovesFromParent()
  {
    ToolGrantPolicy policy = new(new Dictionary<string, string>
    {
      [ToolGrantPolicy.DenyKey] = "exec",
    });

    IReadOnlySet<string> effective = policy.EffectiveTools(Parent);
    Assert.Equal(new HashSet<string>(StringComparer.Ordinal) { "agent.spawn", "web_fetch", "read" }, effective);
    Assert.Empty(policy.WideningViolations(Parent));
  }

  [Fact]
  public void AllowThenDeny_Composes()
  {
    ToolGrantPolicy policy = new(new Dictionary<string, string>
    {
      [ToolGrantPolicy.AllowKey] = "web_fetch; exec",
      [ToolGrantPolicy.DenyKey] = "exec",
    });

    IReadOnlySet<string> effective = policy.EffectiveTools(Parent);
    Assert.Equal(new HashSet<string>(StringComparer.Ordinal) { "web_fetch" }, effective);
  }
}
