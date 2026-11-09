using eThangAgent.ToolDomain;

namespace eThangAgent.Tool.Domain.Tests;

/// <summary>The cost guidance the guide states, pinned so it cannot drift back to
///     asking for generous budgets. The principle is deliberately abstract: a workspace
///     need not hold code, so no test class, solution, or project is named.</summary>
public class ExecGuideCostGuidanceTests
{
  [Fact]
  public void Guide_No_Longer_Asks_For_Generous_Budgets()
      => Assert.DoesNotContain("Choose generously", ExecGuide.Text, StringComparison.Ordinal);

  [Fact]
  public void Guide_No_Longer_Suggests_A_Larger_Budget_As_The_Fix()
  {
    // The guide may NAME a larger budget only to reject it; what it must never do is
    // prescribe one as the remedy for an overrun.
    Assert.DoesNotContain("re-issue with a larger budget", ExecGuide.Text, StringComparison.OrdinalIgnoreCase);
    Assert.DoesNotContain("increase the budget", ExecGuide.Text, StringComparison.OrdinalIgnoreCase);
    Assert.DoesNotContain("raise the budget", ExecGuide.Text, StringComparison.OrdinalIgnoreCase);
  }

  [Fact]
  public void Guide_Names_The_Elapsed_Line_And_The_Nudge()
  {
    Assert.Contains("[exec: 1.2s of 600s budget]", ExecGuide.Text, StringComparison.Ordinal);
    Assert.Contains("[budget]", ExecGuide.Text, StringComparison.Ordinal);
  }

  [Fact]
  public void Guide_Asks_For_The_Narrowest_Command_That_Answers_The_Question()
  {
    Assert.Contains("narrowest command that answers the question", ExecGuide.Text, StringComparison.Ordinal);
    Assert.Contains("a principle, not a list", ExecGuide.Text, StringComparison.Ordinal);
  }

  [Fact]
  public void Guide_Names_No_Repository_Structure_A_Workspace_May_Not_Have()
  {
    // The guidance must hold for a workspace that is not code at all.
    Assert.DoesNotContain("test class", ExecGuide.Text, StringComparison.OrdinalIgnoreCase);
    Assert.DoesNotContain("whole solution", ExecGuide.Text, StringComparison.OrdinalIgnoreCase);
    Assert.DoesNotContain("project file", ExecGuide.Text, StringComparison.OrdinalIgnoreCase);
  }
}
