using eThangAgent.Agent.Application;
using eThangAgent.AgentDomain;
using eThangAgent.Composition;
using eThangAgent.ConversationDomain;
using eThangAgent.Desktop.Streaming;
using eThangAgent.Desktop.ViewModels;
using eThangAgent.SharedKernel;

namespace eThangAgent.Desktop.Tests;

/// <summary>
/// The freeze regression guard: the production turn pipeline must time-slice
/// per-token deltas so one long response produces bounded sink deliveries per
/// window - not one marshaled UI-thread job per token. Pins the integration
/// end to end: a burst of rapid deltas yields far fewer deliveries than deltas,
/// content survives byte-complete and in order, and structural events keep
/// their exact positions between text slices.
/// </summary>
public class StreamCoalescingIntegrationTests
{
  /// <summary>Records every sink delivery for inspection.</summary>
  private sealed class RecordingSink : IStreamSink
  {
    public List<UiStreamEvent> Received { get; } = [];

    public Task DeliverAsync(UiStreamEvent evt)
    {
      Received.Add(evt);
      return Task.CompletedTask;
    }
  }

  private static AgentSessionViewModel Build(TurnRunner runner, RecordingSink sink)
  {
    return new AgentSessionViewModel(
        runner,
        new RootSessionLifecycle(new TestFixtures.StubStore()),
        AgentId.NewId(),
        new Conversation(),
        provider: "OpenRouter",
        modelId: "test/model",
        new AgentSessionViewModelOptions { WorkspaceRoot = @"C:\work\demo", UiStreamSink = sink.DeliverAsync });
  }

  /// <summary>A runner emitting <paramref name="deltaCount"/> deltas back-to-back -
  ///     the no-gap burst shape that starved the UI thread before coalescing.</summary>
  private static TurnRunner BurstRunner(int deltaCount)
  {
    return Runner;

    Task<Result<string>> Runner(SendMessageCommand _c, CancellationToken _ct, TurnCallbacks? callbacks, Action<string>? _n)
    {
      if (callbacks?.OnContentDelta is { } delta)
      {
        for (int i = 0; i < deltaCount; i++)
        {
          delta("tok ");
        }
      }

      return Task.FromResult(Result.Success("burst done"));
    }
  }

  /// <summary>Names the production change that would fail this test: unwiring the
  ///     coalescer from the turn pipeline (or regressing the pump routing) delivers
  ///     one event per delta - 400 here vs. the bounded handful coalescing produces.
  ///     The threshold stays far above one window's worth of deliveries so a slow CI
  ///     machine that stalls mid-burst still cannot fail the test: stalls create
  ///     more windows, and 40 windows at 80 ms means 3+ seconds of stalls.</summary>
  [Fact]
  public async Task Rapid_Delta_Burst_Produces_Bounded_Sink_Deliveries()
  {
    RecordingSink sink = new();
    AgentSessionViewModel vm = Build(BurstRunner(400), sink);

    await vm.SubmitAsync("q");
    await vm.WaitForTurnAsync();

    int deliveries = sink.Received.Count;
    Assert.True(deliveries <= 40,
        $"expected bounded deliveries, got {deliveries} (per-token regression?)");

    string text = string.Concat(sink.Received
        .OfType<UiStreamEvent.Delta>()
        .Select(d => d.Text));
    Assert.Equal(400 * "tok ".Length, text.Length); // nothing lost, nothing doubled
  }

  /// <summary>Structural events split text slices: coalescing must never merge a
  ///     delta across a tool-call boundary nor reorder anything. Invariant guard
  ///     for the wiring (the burst test above is the RED driver).</summary>
  [Fact]
  public async Task Structural_Events_Keep_Their_Positions_Between_Text_Slices()
  {
    RecordingSink sink = new();
#pragma warning disable IDE0060, S1172 // Delegate-shape parameters are unused by design.
    static Task<Result<string>> Runner(SendMessageCommand _command, CancellationToken _ct, TurnCallbacks? callbacks, Action<string>? _onNotice)
    {
      callbacks?.OnContentDelta?.Invoke("pre ");
      callbacks?.OnToolCall?.Invoke("read", "{}", 0, 1);
      callbacks?.OnContentDelta?.Invoke("post");
      return Task.FromResult(Result.Success("done"));
    }
#pragma warning restore IDE0060, S1172
    AgentSessionViewModel vm = Build(Runner, sink);

    await vm.SubmitAsync("q");
    await vm.WaitForTurnAsync();

    Assert.Equal(3, sink.Received.Count);
    Assert.Equal("pre ", Assert.IsType<UiStreamEvent.Delta>(sink.Received[0]).Text);
    _ = Assert.IsType<UiStreamEvent.ToolCallEvent>(sink.Received[1]);
    Assert.Equal("post", Assert.IsType<UiStreamEvent.Delta>(sink.Received[2]).Text);
  }
}
