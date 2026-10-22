using eThangAgent.SharedKernel;

namespace eThangAgent.AgentDomain.Tests;

/// <summary>The progress wake is GONE from agent.wait: a child's progress beats fire on
///     every tool call, so a wait that woke on them never rests. The ChildProgressEvent
///     itself survives (it feeds the watchdog's idle clock and the Desktop panel) — but
///     it no longer wakes a wait. These tests pin both halves of that contract: the
///     vocabulary refuses the retired token, and the adapter never registers a progress
///     signal.</summary>
public class WaitProgressRemovalTests
{
  private sealed class RecordingEvents : IAgentEvents
  {
    public IAgentEventSubscriber? Subscriber { get; private set; }

    public IDisposable Subscribe(IAgentEventSubscriber subscriber)
    {
      Subscriber = subscriber;
      return new FakeLease(this);
    }

    public void Publish(ChildEvent evt) => Subscriber?.OnEvent(evt);

    private sealed class FakeLease(RecordingEvents owner) : IDisposable
    {
      public void Dispose() => owner.Subscriber = null;
    }
  }

  private static readonly DateTimeOffset Now = new(2026, 10, 22, 12, 0, 0, TimeSpan.Zero);

  [Fact]
  public void ProgressEvent_RegistersNoWakeSignal()
  {
    RecordingEvents events = new();
    WaitWakeHub hub = new();
    using ChildEventWakeAdapter adapter = new(events, hub);
    List<WakeSignal> seen = [];
    using IDisposable lease = hub.Subscribe(seen.Add);
    AgentId child = AgentId.NewId();

    events.Publish(new ChildProgressEvent(child, Now, ChildPhase.ToolExec, "tool:read"));

    Assert.Empty(seen);
  }

  [Fact]
  public void ProgressEvent_DoesNotWakeWhileOtherReasonsStillDo()
  {
    // The progress case must be GONE, not merely coalesced: an idle alert still wakes,
    // a progress beat never does.
    RecordingEvents events = new();
    WaitWakeHub hub = new();
    using ChildEventWakeAdapter adapter = new(events, hub);
    List<WakeSignal> seen = [];
    using IDisposable lease = hub.Subscribe(seen.Add);
    AgentId child = AgentId.NewId();

    events.Publish(new ChildProgressEvent(child, Now, ChildPhase.ModelCall, "iteration"));
    events.Publish(new ChildIdleAlertEvent(child, Now, TimeSpan.FromMinutes(3), "ModelCall"));

    WakeSignal signal = Assert.Single(seen);
    Assert.Equal(WakeReason.Idle, signal.Reason);
  }

  [Theory]
  [InlineData("progress")]
  [InlineData("idle,progress")]
  public void WakeFilter_RejectsTheRetiredProgressToken(string raw)
  {
    Result<WakeFilter> parsed = WakeFilter.Parse(raw);

    Assert.False(parsed.IsSuccess);
    Assert.Equal("InvalidActionInput", parsed.Error.Code);
    Assert.Contains("progress", parsed.Error.Message, StringComparison.Ordinal);
  }

  [Fact]
  public void WakeFilter_AllCarriesTheRemainingReasons()
  {
    Result<WakeFilter> parsed = WakeFilter.Parse("all");

    Assert.True(parsed.IsSuccess);
    Assert.True(parsed.Value.Accepts(WakeReason.Idle));
    Assert.True(parsed.Value.Accepts(WakeReason.StreamStall));
    Assert.True(parsed.Value.Accepts(WakeReason.StateWrite));
    Assert.True(parsed.Value.Accepts(WakeReason.Budget));
    Assert.True(parsed.Value.Accepts(WakeReason.Mailbox));
  }
}
