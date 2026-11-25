using FlaUI.Core.Definitions;

namespace eThangAgent.ComputerUse.Host.Tests;

/// <summary>Walk order decides what the first page of an observe shows. A browser
///     window's control-view children lead with the tab strip, toolbar and omnibox, so a
///     page-sized slice of the walk is chrome rather than content and the document sits
///     far past it. Content-bearing subtrees are walked first.</summary>
public class TreeWalkOrderTests
{
  [Fact]
  public void ContentBearingRoles_RankBeforeChrome()
  {
    Assert.True(UiaWalk.RankFor(ControlType.Document) < UiaWalk.RankFor(ControlType.ToolBar));
    Assert.True(UiaWalk.RankFor(ControlType.Pane) < UiaWalk.RankFor(ControlType.Tab));
    Assert.True(UiaWalk.RankFor(ControlType.List) < UiaWalk.RankFor(ControlType.MenuBar));
  }

  [Fact]
  public void UnknownRole_RanksWithContent() =>
    Assert.Equal(UiaWalk.RankFor(ControlType.Document), UiaWalk.RankFor(ControlType.Custom));

  [Fact]
  public void StableWithinARank()
  {
    ControlType[] children = [ControlType.Tab, ControlType.Document, ControlType.ToolBar, ControlType.Pane];
    ControlType[] ordered = [.. UiaWalk.OrderChildren(children)];
    Assert.Equal([ControlType.Document, ControlType.Pane, ControlType.Tab, ControlType.ToolBar], ordered);
  }
}
