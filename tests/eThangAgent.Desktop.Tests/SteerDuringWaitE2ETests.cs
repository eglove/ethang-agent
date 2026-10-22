using eThangAgent.Desktop.ViewModels;

namespace eThangAgent.Desktop.Tests;

/// <summary>The plan's proof: a parent blocked in agent.wait wakes within seconds of
///     the user typing. The child's provider response is delayed by the mock (the child
///     stays status=running), the parent waits with the default wakeOn (the always-on
///     user wake — the child's progress beats are not wake-worthy), the test
///     posts user input through the session view-model once the parent's SECOND
///     provider request (the wait call) has been served, and the parent's post-wake
///     provider request carries the wake annotation plus the steering message.</summary>
/// <remarks>Sync point: the mock's request count, not the transcript. The transcript's
///     tool-call entry rides the stream coalescer and can lag the actual exec by
///     seconds under module-parallel load (observed 8.6 s), while the mock serves the
///     request the moment the loop sends it — the wait exec starts within
///     milliseconds of that, and the sticky user wake (WaitWakeHub) covers a steer
///     that beats the wait's subscribe.</remarks>
[Collection("Desktop E2E")]
public class SteerDuringWaitE2ETests
{
  private static string RawCompletion(string content) => E2E.RawCompletion(content);

  [Fact]
  public async Task UserSteer_WakesBlockedWait_AndReachesParent()
  {
    using E2E.HostHarness host = new();
    _ = await host.StartAsync();

    // Child: its ONLY provider response is DELAYED 8 s — long enough for the steer to
    // land while the parent is inside agent.wait, short enough to keep the test fast.
    // The mock serves requests CONCURRENTLY (MockConcurrencyTests), so the parent's
    // wait request is served while the child's delayed response is still pending, and
    // the sticky user wake covers a steer that beats the wait's subscribe.
    _ = host.Mock.DelayResponsesForModel("mock/sub-model", TimeSpan.FromSeconds(8));
    _ = host.Mock.ReturnsForModel("mock/sub-model", RawCompletion("child eventually reports"));

    // Parent turn 1: spawn a child on the delayed model, then wait with the DEFAULT
    // wakeOn (the always-on user wake) and a 90 s soft timeout (well under the exec
    // budget). The child's progress beats are NOT wake-worthy, so nothing but the
    // user steer can wake this wait — the behavior under test is exactly that
    // steering wakes the parent.
    _ = host.Mock.ReturnsForModel(E2E.SessionModel,
        E2E.ExecToolCall("p1", E2E.ExecProgram("return Tools.Invoke(\"agent.spawn\", new { timeoutSeconds = 60, taskPrompt = \"Stall then report.\", model = \"mock/sub-model\", label = \"stall\" });")));
    _ = host.Mock.ReturnsForModel(E2E.SessionModel,
        E2E.ExecToolCall("p2", E2E.ExecProgram("return Tools.Invoke(\"agent.wait\", new { timeoutSeconds = 90, id = \"{{child_id}}\" });")));
    // The parent's next request after the wake: final text.
    _ = host.Mock.ReturnsForModel(E2E.SessionModel, RawCompletion("woke and acted"));

    Task parentTurn = host.Vm.SubmitAsync("delegate and wait")
        .WaitAsync(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken);

    // While the parent is inside the wait (IsBusy), the user types. The input path
    // posts to the signaling inbox — the unconditional user wake. The steer waits
    // until the mock has served the parent's SECOND request (the wait call): the
    // wait exec starts within milliseconds of that, and the sticky user wake covers
    // a steer that beats the wait's subscribe. A steer posted EARLIER is drained at
    // the safe point between the spawn exec and the wait exec — delivered as an
    // ordinary message (correct product behavior, but no wake annotation to assert).
    await SpinUntilBusyAsync(host.Vm, TimeSpan.FromSeconds(10)).ConfigureAwait(true);
    await SpinUntilSecondRequestAsync(host.Mock, TimeSpan.FromSeconds(30)).ConfigureAwait(true);
    await host.Vm.SubmitAsync("the child is spinning — take over").ConfigureAwait(true);

    await parentTurn.ConfigureAwait(true);

    // The parent's post-wake request carries the wake annotation and the user message.
    List<string> parentBodies = [.. host.Mock.RequestBodies.Where(body => MockOpenRouterServer.TryGetRequestModel(body) == E2E.SessionModel)];
    Assert.True(parentBodies.Count >= 3, $"expected >= 3 parent requests, got {parentBodies.Count}");
    string postWake = E2E.FindToolMessageContaining(parentBodies, "[wait: woken reason=user-message]");
    Assert.Contains("status=running", postWake, StringComparison.Ordinal);
    // The steering message enters the conversation as a USER message (drained from the
    // inbox at the wake), not inside the exec tool result — assert it in the request body.
    Assert.Contains(parentBodies, body => body.Contains("the child is spinning", StringComparison.Ordinal));
  }

  /// <summary>Spins until the mock has served TWO requests for the session model —
  ///     the spawn call and the wait call. The wait exec follows within milliseconds,
  ///     and the sticky user wake covers the subscribe gap.</summary>
  private static async Task SpinUntilSecondRequestAsync(MockOpenRouterServer mock, TimeSpan limit)
  {
    System.Diagnostics.Stopwatch sw = System.Diagnostics.Stopwatch.StartNew();
    while (mock.RequestBodies.Count(body => MockOpenRouterServer.TryGetRequestModel(body) == E2E.SessionModel) < 2)
    {
      if (sw.Elapsed > limit)
      {
        Assert.Fail("the parent never issued its wait request");
      }

      await Task.Delay(25, TestContext.Current.CancellationToken).ConfigureAwait(true);
    }
  }

  private static async Task SpinUntilBusyAsync(AgentSessionViewModel vm, TimeSpan limit)
  {
    System.Diagnostics.Stopwatch sw = System.Diagnostics.Stopwatch.StartNew();
    while (!vm.IsBusy)
    {
      if (sw.Elapsed > limit)
      {
        Assert.Fail("the parent turn never became busy");
      }

      await Task.Delay(25, TestContext.Current.CancellationToken).ConfigureAwait(true);
    }
  }
}
