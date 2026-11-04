using eThangAgent.Desktop.ViewModels;

namespace eThangAgent.Desktop.Tests;

/// <summary>Issue #307's settled cell decisions: D7 (a closed cell persists as a finished card
///     until dismissed, never reflowing) and D5 (a non-streaming agent is visually distinct).
///     <para>These are the decisions the issue settled, so they are asserted directly rather
///     than inferred from the layout: a grid that rendered correctly while dropping a finished
///     card would still have lost the record of what ran.</para>
///     <para>ISSUE #332 RETIRED THE 'SEQUENTIAL ADVANCE REUSES A CELL' HALF OF D2. Every route
///     that starts a session appends, so the tests here assert the APPEND and the RETIRE, and
///     the old in-place swap they used to describe no longer exists.</para></summary>
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

    AgentCellViewModel? finished = tab.MarkSessionFinished(first.RootId);

    Assert.Same(first, finished);
    Assert.True(first.IsFinished);
    Assert.Same(first, tab.Cells[0]);
    Assert.True(tab.IsFullyFinished);
  }

  /// <summary>D7: the card is removed when the user closes it, whatever state its agent is in -
  ///     an agent that never settles is the one a human most needs a way out of.</summary>
  [Fact]
  public void DismissingACell_RemovesIt_InAnyRunState()
  {
    AgentTabViewModel tab = TestFixtures.CreateTab();
    AgentCellViewModel running = tab.AddCell(
        TestFixtures.FakeSessionFor(TestFixtures.CreateViewModel()),
        TestFixtures.CreateViewModel());

    Assert.True(tab.DismissCell(running), "a running cell is closable");

    _ = Assert.Single(tab.Cells);
  }

  /// <summary>A cell that belongs to another tab is refused, so a click can never remove a card
  ///     this tab does not hold.</summary>
  [Fact]
  public void DismissingAForeignCell_IsRefused()
  {
    AgentTabViewModel tab = TestFixtures.CreateTab();
    AgentTabViewModel other = TestFixtures.CreateTab();

    Assert.False(tab.DismissCell(other.Cells[0]));
    _ = Assert.Single(tab.Cells);
  }

  /// <summary>THE LAST CELL IS CLOSABLE (issue #339): the tab is then empty, which is an
  ///     ordinary state - it still knows its workspace, so the next agent has somewhere to go,
  ///     and its session surface is null rather than a throw.</summary>
  [Fact]
  public void TheLastCell_IsDismissible_LeavingATabThatStillKnowsItsWorkspace()
  {
    AgentTabViewModel tab = TestFixtures.CreateTab();
    string root = tab.WorkspaceRoot;

    Assert.True(tab.DismissCell(tab.Cells[0]));

    Assert.Empty(tab.Cells);
    Assert.True(tab.IsEmpty);
    Assert.Null(tab.LiveContainer);
    Assert.Null(tab.LiveViewModel);
    Assert.Equal(root, tab.WorkspaceRoot);
    Assert.Equal(root, tab.Subtitle);
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

  /// <summary>A new cell is APPENDED and leaves the existing ones alone, so the grid's
  ///     positions keep their meaning.
  ///     <para>THE TAB'S OWN SESSION DOES NOT MOVE (issue #332): nothing was superseded, so the
  ///     first cell is still the live one every pre-#307 surface addresses.</para></summary>
  [Fact]
  public void AddingACell_AppendsIt_AndLeavesTheOthersInPlace()
  {
    AgentTabViewModel tab = TestFixtures.CreateTab();
    AgentCellViewModel first = tab.Cells[0];
    AgentSessionViewModel second = TestFixtures.CreateViewModel();

    AgentCellViewModel added = tab.AddCell(TestFixtures.FakeSessionFor(second), second);

    Assert.Equal(2, tab.Cells.Count);
    Assert.Same(first, tab.Cells[0]);
    Assert.Same(added, tab.Cells[1]);
    Assert.Same(first.ViewModel, tab.ViewModel);
    Assert.Same(first.Container, tab.Container);
  }

  /// <summary>A SUPERSEDED cell is retired, not removed (issue #332): its card is marked
  ///     finished, it stops naming the container the shell is about to dispose, and the tab's
  ///     own session moves to the cell that replaced it.
  ///     <para>THE THREE FACTS TOGETHER ARE THE WHOLE CONTRACT. Marking finished without
  ///     releasing the container leaves a card over a disposed provider; releasing the
  ///     container without marking finished leaves a live-looking card over nothing; and
  ///     neither of them moves <c>Container</c>, which is what every later lookup reads.</para>
  ///     <para>AND THE RAIL IS UNTOUCHED (D6): it is the tab's, so a retirement must not
  ///     rebuild a pane the user is reading.</para></summary>
  [Fact]
  public void RetiringACell_FinishesItsCard_ReleasesItsContainer_AndMovesTheTabsSession()
  {
    AgentTabViewModel tab = TestFixtures.CreateTab();
    AgentCellViewModel first = tab.Cells[0];
    AgentSessionViewModel fresh = TestFixtures.CreateViewModel();
    AgentCellViewModel successor = tab.AddCell(TestFixtures.FakeSessionFor(fresh), fresh);
    Guid retired = first.RootId.Value;
    WorkbenchViewModel rail = tab.Workbench;

    tab.RetireCell(first);

    // The card is KEPT - D7 keeps the position - and it reads as a finished record.
    Assert.Equal(2, tab.Cells.Count);
    Assert.Same(first, tab.Cells[0]);
    Assert.True(first.IsFinished);
    Assert.Null(first.Container);
    Assert.Contains("closed", first.ViewModel.ChatInputPlaceholder, StringComparison.Ordinal);
    // The tab's own session is the successor now, so no lookup can reach the retired id.
    Assert.NotEqual(retired, tab.Container.RootId.Value);
    Assert.Same(fresh, tab.ViewModel);
    Assert.Same(successor, tab.Cells[1]);
    // D6: the rail is the tab's, so a retirement must not rebuild it.
    Assert.Same(rail, tab.Workbench);
  }

  /// <summary>A retirement RE-RAISES the tab's own computed surfaces (issue #332): the status
  ///     bar and the model controls bind to <c>ViewModel</c> and the rail's identity to
  ///     <c>Container</c>, and both are computed over the cells.
  ///     <para>THE NOTIFICATION IS THE ASSERTION, not the new value: a binding that never hears
  ///     the property moved keeps rendering the retired session, which is exactly the stale
  ///     surface a value-only test is green through.</para></summary>
  [Fact]
  public void RetiringACell_ReRaisesTheTabsComputedSurfaces()
  {
    AgentTabViewModel tab = TestFixtures.CreateTab();
    AgentSessionViewModel fresh = TestFixtures.CreateViewModel();
    _ = tab.AddCell(TestFixtures.FakeSessionFor(fresh), fresh);
    List<string> raised = [];
    tab.PropertyChanged += (_, e) => raised.Add(e.PropertyName ?? "");

    tab.RetireCell(tab.Cells[0]);

    Assert.Contains(nameof(AgentTabViewModel.ViewModel), raised);
    Assert.Contains(nameof(AgentTabViewModel.Container), raised);
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
