using eThangAgent.Agent.Application;
using eThangAgent.AgentDomain;
using eThangAgent.Desktop.ViewModels;
using eThangAgent.SharedKernel;

namespace eThangAgent.Desktop.Tests;

/// <summary>The production enactment seam (spec D3/D4): what the app can honestly do
///     when the governor picks a rung. A rung the app cannot perform is REFUSED with a
///     NAMED error, and the governor records that refusal - the trail never claims an
///     action that did not happen. These refusals had no test at all.</summary>
public sealed class ResourceHostEnactmentTests
{
  [Fact]
  public async Task Steer_IsRefusedWithANamedError()
  {
    ResourceHostEnactment enactment = new(_ => new ResourceHostRegistration("c1", null));

    Result<string> result = await enactment.EnactAsync("c1", ResourceAction.Steer,
        "under pressure", TestContext.Current.CancellationToken).ConfigureAwait(true);

    Assert.False(result.IsSuccess);
    Assert.Equal("RungRefused", result.Error.Code);
    Assert.Contains("steer", result.Error.Message, StringComparison.OrdinalIgnoreCase);
  }

  [Fact]
  public async Task RestartApp_IsRefusedWithANamedError()
  {
    ResourceHostEnactment enactment = new(_ => new ResourceHostRegistration("c1", null));

    Result<string> result = await enactment.EnactAsync("c1", ResourceAction.RestartApp,
        "under pressure", TestContext.Current.CancellationToken).ConfigureAwait(true);

    Assert.False(result.IsSuccess);
    Assert.Equal("RungRefused", result.Error.Code);
    Assert.Contains("restart", result.Error.Message, StringComparison.OrdinalIgnoreCase);
  }

  /// <summary>The 'kill a program' rung kills the whole conversation HOST process,
  ///     so the words the user reads must say what actually happens. The domain enum
  ///     keeps its spec name (spec D3 owns the ladder); the user-facing vocabulary is
  ///     renamed, and the reason is recorded on the enactment class.</summary>
  [Fact]
  public void KillProgram_IsNamedForWhatItActuallyDoes()
  {
    // The rung is a MISNOMER in production: it stops the conversation's host
    // process, not one program inside it. The rendered phrase must say so.
    ResourceIndicatorViewModel vm = new();
    vm.Update(
        new ResourceGovernorTick(ResourceLevel.Act, "c1", ResourceAction.KillProgram,
            6_000_000_000L, 8_000_000_000L),
        [new ResourceObservation("c1", 6_000_000_000L, [])],
        [new WatchdogEvent(Guid.NewGuid(), null, WatchdogEventKind.ResourceActed,
            "conversation c1 at 6000 MB of its 8000 MB share (Act; harness budget 8000 MB) -> KillProgram",
            0, 6000, new DateTimeOffset(2026, 10, 18, 12, 0, 0, TimeSpan.Zero))]);

    string line = Assert.Single(vm.RecentDecisions);
    Assert.DoesNotContain("KillProgram", line, StringComparison.Ordinal);
    Assert.Contains("host process", line, StringComparison.OrdinalIgnoreCase);
  }

  [Fact]
  public async Task AConversationThatIsNotOpen_IsRefusedWithANamedError()
  {
    ResourceHostEnactment enactment = new(_ => null);

    Result<string> result = await enactment.EnactAsync("gone", ResourceAction.KillProgram,
        "under pressure", TestContext.Current.CancellationToken).ConfigureAwait(true);

    Assert.False(result.IsSuccess);
    Assert.Equal("ConversationNotOpen", result.Error.Code);
  }
}
