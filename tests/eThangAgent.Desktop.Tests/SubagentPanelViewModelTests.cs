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
}
