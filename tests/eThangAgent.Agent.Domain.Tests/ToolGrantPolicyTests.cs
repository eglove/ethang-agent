namespace eThangAgent.AgentDomain.Tests;

/// <summary>Grant semantics: allow∩parent − deny, widening rejected, absent grants pass
///     the default surface through untouched (D9/A5). Since exec became the only top-level
///     tool (issue #288), a narrowing grant always keeps the DOOR when the parent holds it:
///     every other capability is reached from inside an exec program, so a set without the
///     door would grant nothing at all - and the door is never a widening, because a parent
///     without it keeps a child without it.</summary>
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
    Assert.Equal(new HashSet<string>(StringComparer.Ordinal) { "web_fetch", "read", "exec" }, effective);
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
    // An EXPLICIT deny of the door is honored (issue #288): the requester named exec, and
    // overriding a deliberate deny would be the clamp the grant policy forbids. The child
    // then reaches nothing, which is exactly what denying the only door means.
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

  /// <summary>Denying exec denies the door itself: the request is honored exactly, and the
  ///     child then reaches nothing (no capability is top-level). Pinned so the door rule can
  ///     never be mistaken for a clamp that overrides an explicit deny.</summary>
  [Fact]
  public void DenyingTheDoor_IsHonored_AndLeavesTheChildWithNoTool()
  {
    ToolGrantPolicy policy = new(new Dictionary<string, string>
    {
      [ToolGrantPolicy.AllowKey] = "web_fetch",
      [ToolGrantPolicy.DenyKey] = "exec",
    });

    IReadOnlySet<string> effective = policy.EffectiveTools(Parent);
    Assert.Equal(new HashSet<string>(StringComparer.Ordinal) { "web_fetch" }, effective);
  }

  /// <summary>The door is carried only when the parent holds it: a parent without exec keeps
  ///     a child without exec, so keeping the door is never a widening.</summary>
  [Fact]
  public void Door_IsNotAdded_WhenTheParentLacksIt()
  {
    IReadOnlySet<string> parentWithoutDoor = new HashSet<string>(StringComparer.Ordinal)
      { "agent.spawn", "web_fetch", "read" };
    ToolGrantPolicy policy = new(new Dictionary<string, string>
    {
      [ToolGrantPolicy.AllowKey] = "web_fetch",
    });

    IReadOnlySet<string> effective = policy.EffectiveTools(parentWithoutDoor);
    Assert.Equal(new HashSet<string>(StringComparer.Ordinal) { "web_fetch" }, effective);
  }
}
