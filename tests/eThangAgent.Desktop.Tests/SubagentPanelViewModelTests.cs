using eThangAgent.AgentDomain;
using eThangAgent.ConversationDomain;
using eThangAgent.Desktop.ViewModels;

namespace eThangAgent.Desktop.Tests;

/// <summary>A minimal in-process child-event stream for panel tests: synchronous
///     fan-out with lease disposal, the same contract InProcessAgentEvents serves.</summary>
internal sealed class TestEventStream : IAgentEvents
{
  private readonly List<IAgentEventSubscriber> _subscribers = [];

  public IDisposable Subscribe(IAgentEventSubscriber subscriber)
  {
    _subscribers.Add(subscriber);
    return new Lease(_subscribers, subscriber);
  }

  public void Publish(ChildEvent evt)
  {
    foreach (IAgentEventSubscriber subscriber in _subscribers.ToArray())
    {
      subscriber.OnEvent(evt);
    }
  }

  private sealed class Lease(List<IAgentEventSubscriber> subscribers, IAgentEventSubscriber subscriber) : IDisposable
  {
    public void Dispose() => _ = subscribers.Remove(subscriber);
  }
}

/// <summary>The subagent split view's view-model: rows arrive by SUBSCRIPTION to the
///     child-event stream (started adds, progress phases, settled marks done), the
///     newest row expands, and the store-polled refresh appends persisted messages
///     incrementally. Pure view-model tests — no window needed.</summary>
public class SubagentPanelViewModelTests
{
  private static ChildStartedEvent Started(AgentId id, string model = "test/model")
      => new(id, DateTimeOffset.UtcNow, null, model, 0);

  [Fact]
  public void Attach_Without_Events_Constructs_Nothing_And_Panel_Has_No_Rows()
  {
    using SubagentPanelViewModel panel = new(store: null);
    panel.Attach(events: null);

    Assert.Empty(panel.Rows);
    Assert.False(panel.HasRows);
  }

  [Fact]
  public void Started_Event_Adds_Row_And_Expands_It()
  {
    using SubagentPanelViewModel panel = new(store: null);
    panel.Attach(new TestEventStream());
    AgentId id = AgentId.NewId();

    panel.OnEvent(Started(id));

    SubagentRowViewModel row = Assert.Single(panel.Rows);
    Assert.Equal(id, row.Id);
    Assert.True(row.IsRunning);
    Assert.True(row.IsExpanded);
    Assert.True(panel.HasRows);
  }

  [Fact]
  public void Same_Id_Started_Twice_Adds_One_Row_Watchdog_Retry()
  {
    using SubagentPanelViewModel panel = new(store: null);
    panel.Attach(new TestEventStream());
    AgentId id = AgentId.NewId();

    panel.OnEvent(Started(id));
    panel.OnEvent(Started(id));

    _ = Assert.Single(panel.Rows);
  }

  [Fact]
  public void Progress_Updates_Phase_Label()
  {
    using SubagentPanelViewModel panel = new(store: null);
    panel.Attach(new TestEventStream());
    AgentId id = AgentId.NewId();
    panel.OnEvent(Started(id));

    panel.OnEvent(new ChildProgressEvent(id, DateTimeOffset.UtcNow, ChildPhase.ToolExec, "tool:exec"));

    Assert.Equal("tool:exec", panel.Rows[0].PhaseLabel);
    Assert.Equal("tool:exec", panel.Rows[0].StatusDisplay);
  }

  [Fact]
  public void Settled_Marks_Terminal_With_Status_And_Reason()
  {
    using SubagentPanelViewModel panel = new(store: null);
    panel.Attach(new TestEventStream());
    AgentId id = AgentId.NewId();
    panel.OnEvent(Started(id));

    panel.OnEvent(new ChildSettledEvent(id, DateTimeOffset.UtcNow, AgentStatus.Failed,
        AgentFailureReason.ProviderError, 0));

    SubagentRowViewModel row = panel.Rows[0];
    Assert.False(row.IsRunning);
    Assert.Contains("failed", row.StatusDisplay, StringComparison.Ordinal);
    Assert.Contains("ProviderError", row.StatusDisplay, StringComparison.Ordinal);
  }

  [Fact]
  public void Settled_Completed_Shows_Done()
  {
    using SubagentPanelViewModel panel = new(store: null);
    panel.Attach(new TestEventStream());
    AgentId id = AgentId.NewId();
    panel.OnEvent(Started(id));

    panel.OnEvent(new ChildSettledEvent(id, DateTimeOffset.UtcNow, AgentStatus.Completed, null, 10));

    Assert.Equal("done", panel.Rows[0].StatusDisplay);
    Assert.False(panel.Rows[0].IsRunning);
  }

  [Fact]
  public async Task Refresh_Appends_Persisted_Messages_Incrementally()
  {
    AgentId id = AgentId.NewId();
    List<Message> transcript =
    [
      new Message(Role.User, "check the claims", DateTimeOffset.UtcNow),
    ];
    TestFixtures.TranscriptStore store = new(transcript);
    using SubagentPanelViewModel panel = new(store);
    panel.Attach(new TestEventStream());
    panel.OnEvent(Started(id));

    await panel.RefreshRunningTranscriptsAsync().ConfigureAwait(true);
    _ = Assert.Single(panel.Rows[0].Transcript.Entries);

    // A new persisted message lands on the next refresh — appended, not replayed.
    transcript.Add(new Message(Role.Assistant, "working on it", DateTimeOffset.UtcNow));
    await panel.RefreshRunningTranscriptsAsync().ConfigureAwait(true);
    Assert.Equal(2, panel.Rows[0].Transcript.Entries.Count);
  }

  [Fact]
  public void Dispose_Releases_The_Stream_Lease()
  {
    TestEventStream stream = new();
    using SubagentPanelViewModel panel = new(store: null);
    panel.Attach(stream);
    panel.Dispose();

    // Publishing after dispose must not add rows (the lease is gone).
    stream.Publish(Started(AgentId.NewId()));
    Assert.Empty(panel.Rows);
  }

  [Fact]
  public void Attach_Marshal_Wraps_Every_Event_Application()
  {
    // CRASH-FIX regression (commit 884fc61): events publish on stream threads while
    // Rows is bound to the accordion — the shell must marshal every application onto
    // the UI thread. The wrapper is the seam that makes that true; a panel attached
    // without one mutates Rows on the publishing thread.
    TestEventStream stream = new();
    using SubagentPanelViewModel panel = new(store: null);
    List<ChildEvent> marshaled = [];
    panel.Attach(stream, apply => evt =>
    {
      marshaled.Add(evt);
      apply(evt);
    });

    stream.Publish(Started(AgentId.NewId()));

    _ = Assert.Single(marshaled); // the event reached the panel THROUGH the wrapper
    _ = Assert.Single(panel.Rows);
  }

  [Fact]
  public async Task Refresh_Survives_A_Row_Added_Mid_Refresh()
  {
    // CRASH-FIX regression (commit 884fc61): the 500 ms timer enumerated Rows while
    // a child event added one mid-enumeration — 'Collection was modified' escaped the
    // async-void tick and killed the dispatcher. The refresh must enumerate a
    // snapshot: the newcomer is skipped this pass and picked up on the next.
    AgentId first = AgentId.NewId();
    AgentId second = AgentId.NewId();
    List<Message> transcript = [new Message(Role.User, "hello", DateTimeOffset.UtcNow)];
    // The store's read adds the second row DURING the enumeration — the race shape.
    TestFixtures.TranscriptStore store = new(transcript);
    using SubagentPanelViewModel panel = new(store);
    TestEventStream stream = new();
    panel.Attach(stream);
    panel.OnEvent(Started(first));

    panel.MidRefreshHook = () => stream.Publish(Started(second));
    await panel.RefreshRunningTranscriptsAsync().ConfigureAwait(true);

    Assert.Equal(2, panel.Rows.Count); // the newcomer landed without throwing
    // Newest-first: the newcomer sits at Rows[0]; the first child's transcript
    // still refreshed despite the mid-refresh addition.
    _ = Assert.Single(panel.Rows.Single(r => r.Id == first).Transcript.Entries);

    // The next refresh picks the newcomer up.
    await panel.RefreshRunningTranscriptsAsync().ConfigureAwait(true);
    _ = Assert.Single(panel.Rows.Single(r => r.Id == second).Transcript.Entries);
  }

  [Fact]
  public void Two_Started_Events_Newest_At_Top()
  {
    using SubagentPanelViewModel panel = new(store: null);
    panel.Attach(new TestEventStream());
    AgentId first = AgentId.NewId();
    AgentId second = AgentId.NewId();

    panel.OnEvent(Started(first));
    panel.OnEvent(Started(second));

    Assert.Equal(2, panel.Rows.Count);
    Assert.Equal(second, panel.Rows[0].Id); // newest launched at top
    Assert.Equal(first, panel.Rows[1].Id);
  }

  [Fact]
  public void First_Child_Takes_The_Open_Expanded_Spot()
  {
    using SubagentPanelViewModel panel = new(store: null);
    panel.Attach(new TestEventStream());
    AgentId id = AgentId.NewId();

    panel.OnEvent(Started(id));

    // Nothing was expanded, so the newcomer takes the open spot.
    Assert.True(panel.Rows[0].IsExpanded);
    Assert.Equal(panel.Rows[0], panel.SelectedRow);
  }

  [Fact]
  public void Settled_Row_Collapses_The_Expander()
  {
    using SubagentPanelViewModel panel = new(store: null);
    panel.Attach(new TestEventStream());
    AgentId id = AgentId.NewId();
    panel.OnEvent(Started(id));

    panel.OnEvent(new ChildSettledEvent(id, DateTimeOffset.UtcNow, AgentStatus.Completed, null, 0));

    Assert.False(panel.Rows[0].IsExpanded);
  }

  [Fact]
  public void Same_Id_Restart_Reactivates_The_Row_Without_Duplicating()
  {
    using SubagentPanelViewModel panel = new(store: null);
    panel.Attach(new TestEventStream());
    AgentId id = AgentId.NewId();
    panel.OnEvent(Started(id));
    panel.OnEvent(new ChildSettledEvent(id, DateTimeOffset.UtcNow, AgentStatus.Completed, null, 0));

    // A second ChildStartedEvent for the SAME id is a done-to-active transition
    // (watchdog restart): the row returns to running and re-opens.
    panel.OnEvent(Started(id));

    SubagentRowViewModel row = Assert.Single(panel.Rows);
    Assert.True(row.IsRunning);
    Assert.Equal("starting", row.StatusDisplay);
    Assert.True(row.IsExpanded);
  }

  [Fact]
  public void Reactivation_Resets_The_Failed_Brush_To_Running()
  {
    using SubagentPanelViewModel panel = new(store: null);
    panel.Attach(new TestEventStream());
    AgentId id = AgentId.NewId();
    panel.OnEvent(Started(id));
    panel.OnEvent(new ChildSettledEvent(id, DateTimeOffset.UtcNow, AgentStatus.Failed,
        AgentFailureReason.ProviderError, 0));

    panel.OnEvent(Started(id));

    Assert.Equal(Avalonia.Media.Brushes.Orange, panel.Rows[0].StatusBrush);
  }

  [Fact]
  public void Second_Child_Does_Not_Steal_Expansion_Or_Selection()
  {
    using SubagentPanelViewModel panel = new(store: null);
    panel.Attach(new TestEventStream());
    AgentId first = AgentId.NewId();
    AgentId second = AgentId.NewId();

    panel.OnEvent(Started(first));
    SubagentRowViewModel firstRow = panel.Rows[0];

    panel.OnEvent(Started(second));

    // A row is already expanded: the newcomer stays collapsed and the
    // user's selection and expanded row are untouched.
    SubagentRowViewModel secondRow = panel.Rows.Single(r => r.Id == second);
    Assert.False(secondRow.IsExpanded);
    Assert.True(firstRow.IsExpanded);
    Assert.Equal(firstRow, panel.SelectedRow);
  }
}
