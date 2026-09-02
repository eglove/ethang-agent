using eThangAgent.CapabilityDomain;

namespace eThangAgent.Capability.Domain.Tests;

/// <summary>Action-name rules. Hyphens are VALID (named decision, W4): the source
///     spec's broadcast vocabulary (agent.notify-subtree / agent.notify-ancestors)
///     requires them, and action names are strings end-to-end — dispatch is a string
///     switch, receipts and grant sets are JSON keys; nothing generates C# identifiers
///     from action names (the old rule's stated reason, verified absent). Dots,
///     spaces, non-ASCII, and leading/trailing hyphens stay invalid.</summary>
public class CapabilityNameRulesTests
{
  [Theory]
  [InlineData("read")]
  [InlineData("Get_Item")]
  [InlineData("a1")]
  [InlineData("notify-subtree")]
  [InlineData("notify-ancestors")]
  [InlineData("tool-allow")]
  public void ValidActionNames_Accepted(string name)
      => Assert.True(CapabilityNameRules.IsValidActionName(name));

  [Theory]
  [InlineData("")]
  [InlineData("read.file")]
  [InlineData("has space")]
  [InlineData("héllo")]
  [InlineData("-leads")]
  [InlineData("trails-")]
  [InlineData("--")]
  public void InvalidActionNames_Rejected(string name)
      => Assert.False(CapabilityNameRules.IsValidActionName(name));
}
