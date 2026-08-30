using eThangAgent.Agent.Application;
using eThangAgent.AgentDomain;
using eThangAgent.Desktop.ViewModels;
using eThangAgent.SharedKernel;

namespace eThangAgent.Desktop.Tests;

/// <summary>Wiring: the turn's OnToolCall/OnToolResult callbacks feed the status bar's
///     tool elapsed display. Last write wins for batched calls; Ready clears at turn end,
///     so mid-turn assertions are captured inside the runner. The VM wraps the runner in
///     OffUiThread, so callbacks run on a worker thread; Status marshals notifications.</summary>
public class ToolElapsedWiringTests
{
  [Fact]
  public async Task Turn_Callbacks_Feed_Status_Tool_Display()
  {
    AgentSessionViewModel?[] vmBox = new AgentSessionViewModel?[1];
    string[] captured = new string[1];
    Task<Result<string>> Run(SendMessageCommand _c, CancellationToken _ct, TurnCallbacks? cb, Action<string>? _n)
    {
      cb?.OnToolCall?.Invoke("read", "{}", 1, 1);
      cb?.OnToolResult?.Invoke("read", "ok", "ok", false);
      captured[0] = vmBox[0]!.Status.ToolDisplay;
      return Task.FromResult(Result.Success("ack"));
    }

    AgentSessionViewModel vm = TestFixtures.CreateViewModel(Run);
    vmBox[0] = vm;

    await vm.SubmitAsync("hi");
    await vm.WaitForTurnAsync();

    Assert.StartsWith("read ", captured[0], StringComparison.Ordinal);
  }

  [Fact]
  public async Task Turn_End_Clears_Tool_Display()
  {
    AgentSessionViewModel vm = TestFixtures.CreateViewModel();

    await vm.SubmitAsync("hi");
    await vm.WaitForTurnAsync();

    Assert.Equal(TurnPhase.Ready, vm.Status.Phase);
    Assert.Equal("", vm.Status.ToolDisplay);
  }

  [Fact]
  public async Task Batched_Tools_Last_Call_Wins()
  {
    AgentSessionViewModel?[] vmBox = new AgentSessionViewModel?[1];
    string[] captured = new string[1];
    Task<Result<string>> Run(SendMessageCommand _c, CancellationToken _ct, TurnCallbacks? cb, Action<string>? _n)
    {
      cb?.OnToolCall?.Invoke("read", "{}", 1, 2);
      cb?.OnToolResult?.Invoke("read", "ok", "ok", false);
      cb?.OnToolCall?.Invoke("bash", "{}", 2, 2);
      cb?.OnToolResult?.Invoke("bash", "ok", "ok", false);
      captured[0] = vmBox[0]!.Status.ToolDisplay;
      return Task.FromResult(Result.Success("done"));
    }

    AgentSessionViewModel vm = TestFixtures.CreateViewModel(Run);
    vmBox[0] = vm;

    await vm.SubmitAsync("hi");
    await vm.WaitForTurnAsync();

    // The last completed tool is what the status line showed mid-turn.
    Assert.StartsWith("bash ", captured[0], StringComparison.Ordinal);
  }
}
