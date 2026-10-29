using eThangAgent.Desktop.ViewModels;

namespace eThangAgent.Desktop.Tests;

/// <summary>Issue #307's settled cell decisions: D7 (a closed cell persists as a finished card
///     until dismissed, never reflowing), D5 (a non-streaming agent is visually distinct), and
///     D2 (a sequential advance reuses a cell; a parallel dispatch adds one).
///     <para>These are the decisions the issue settled, so they are asserted directly rather
///     than inferred from the layout: a grid that rendered correctly while dropping a finished
///     card would still have lost the record of what ran.</para></summary>
public class AgentCellLifecycleTests
{
  /// <summary>D7: a closed cell stays in the grid, marked finished, and keeps its position.
  ///     Reflowing would lose the record of what ran in that slot - in a grid the position
  ///     itself carries meaning, because cell 3 was the third thing you started.</summary>
  [Fact]
  public void AFinishedCell_PersistsInPlace_UntilDismissed()
  {
    AgentTabViewModel tab = TestFixtures.CreateTab();
    AgentCellViewModel first = tab.Cells[0];

    AgentCellViewModel? finished = tab.MarkSessionFinished(first.Container.RootId);

    Assert.Same(first, finished);
    Assert.True(first.IsFinished);
    Assert.Same(first, tab.Cells[0]);
    Assert.True(tab.IsFullyFinished);
  }

  /// <summary>D7: the card is removed only when the user dismisses it - and only once it is
  ///     finished, so a click can never orphan a live session with no surface.</summary>
  [Fact]
  public void DismissingAFinishedCell_RemovesIt_ButARunningOneIsRefused()
  {
    AgentTabViewModel tab = TestFixtures.CreateTab();
    AgentCellViewModel running = tab.AddCell(
        TestFixtures.FakeSessionFor(TestFixtures.CreateViewModel()),
        TestFixtures.CreateViewModel());

    Assert.False(tab.DismissCell(running), "a running cell must not be dismissed");

    Assert.Equal(2, tab.Cells.Count);

    _ = tab.MarkSessionFinished(running.Container.RootId);
    Assert.True(tab.DismissCell(running), "a finished cell is dismissible");
    _ = Assert.Single(tab.Cells);
  }

  /// <summary>The PRIMARY cell is never dismissible: the tab's own identity is that session,
  ///     so removing it would leave a tab whose <c>Container</c> names nothing.</summary>
  [Fact]
  public void ThePrimaryCell_IsNeverDismissible_EvenWhenFinished()
  {
    AgentTabViewModel tab = TestFixtures.CreateTab();
    _ = tab.MarkSessionFinished(tab.Container.RootId);

    Assert.False(tab.DismissCell(tab.Cells[0]));

    _ = Assert.Single(tab.Cells);
  }

  /// <summary>D5: a non-streaming agent is visually distinct from a streaming one. The
  ///     outline is the human half of the watchdog - the harness nudges a hung agent, and the
  ///     outline tells the user which one to look at.</summary>
  [Fact]
  public void TheIdleOutline_FollowsTheSessionsBusyFlag()
  {
    AgentTabViewModel tab = TestFixtures.CreateTab();
    AgentCellViewModel cell = tab.Cells[0];
    Assert.True(cell.IsIdle, "a fresh cell is not streaming");
    Assert.False(cell.IsStreaming);

    cell.ViewModel.IsBusy = true;

    Assert.True(cell.IsStreaming);
    Assert.False(cell.IsIdle);
  }

  /// <summary>A finished card is idle by definition: it can never be streaming, so the
  ///     outline cannot claim a closed agent is working.</summary>
  [Fact]
  public void AFinishedCell_IsNeverStreaming()
  {
    AgentTabViewModel tab = TestFixtures.CreateTab();
    AgentCellViewModel cell = tab.Cells[0];
    cell.ViewModel.IsBusy = true;

    cell.MarkFinished();

    Assert.False(cell.IsIdle, "a finished card is not the idle outline");
  }

  /// <summary>D2: a PARALLEL dispatch adds a cell and leaves the existing ones alone.
  ///     The added cell is appended, so the grid's positions keep their meaning.</summary>
  [Fact]
  public void AddingACell_AppendsIt_AndLeavesTheOthersInPlace()
  {
    AgentTabViewModel tab = TestFixtures.CreateTab();
    AgentCellViewModel primary = tab.Cells[0];
    AgentSessionViewModel second = TestFixtures.CreateViewModel();

    AgentCellViewModel added = tab.AddCell(TestFixtures.FakeSessionFor(second), second);

    Assert.Equal(2, tab.Cells.Count);
    Assert.Same(primary, tab.Cells[0]);
    Assert.Same(added, tab.Cells[1]);
    // The primary-cell surface still names the FIRST cell, so 'the tab's session' is stable.
    Assert.Same(primary.ViewModel, tab.ViewModel);
    Assert.Same(primary.Container, tab.Container);
  }

  /// <summary>D2: a SEQUENTIAL advance replaces the primary cell in place - the same cell
  ///     index, a new session, and the tab itself untouched so its rail and siblings survive.
  ///     <para>This is the half of D2 that #234's tab-swap used to provide, moved one level
  ///     down: one thing replaced, nothing added, and the selection does not move.</para></summary>
  [Fact]
  public void ReplacingThePrimaryCell_SwapsTheSession_AndKeepsTheCellSlot()
  {
    AgentTabViewModel tab = TestFixtures.CreateTab();
    AgentCellViewModel sibling = tab.AddCell(
        TestFixtures.FakeSessionFor(TestFixtures.CreateViewModel()),
        TestFixtures.CreateViewModel());
    Guid retired = tab.Container.RootId.Value;
    WorkbenchViewModel rail = tab.Workbench;
    AgentSessionViewModel fresh = TestFixtures.CreateViewModel();

    tab.ReplacePrimaryCell(TestFixtures.FakeSessionFor(fresh), fresh);

    Assert.Equal(2, tab.Cells.Count);
    Assert.NotEqual(retired, tab.Container.RootId.Value);
    Assert.Same(fresh, tab.ViewModel);
    Assert.Same(sibling, tab.Cells[1]);
    // D6: the rail is the tab's, so a cell swap must not rebuild it.
    Assert.Same(rail, tab.Workbench);
  }

  /// <summary>A tab opened for one workspace shows exactly one cell, titled by the
  ///     workspace directory - the pre-#307 tab shape, which the grid must still produce.
  ///     <para>This is what makes 'one agent looks exactly like today' true rather than
  ///     aspirational.</para></summary>
  [Fact]
  public void OneAgent_LooksExactlyLikeTheOldTab()
  {
    AgentTabViewModel tab = TestFixtures.CreateTab();

    AgentCellViewModel cell = Assert.Single(tab.Cells);
    Assert.Equal("demo", cell.Title);
    Assert.Equal("demo", tab.Title);
    Assert.False(cell.IsFinished);
  }
}
