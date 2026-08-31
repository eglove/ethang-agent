using eThangAgent.Desktop.ViewModels;
using eThangAgent.SharedKernel;

namespace eThangAgent.Desktop.Tests;

/// <summary>Wiring: the turn's OnToolCall/OnToolResult callbacks land in the
///     transcript with elapsed displays attached. The VM wraps the runner in
///     OffUiThread, so callbacks run on a worker thread and stream through the
///     bridge pump; the transcript is asserted after the turn drains. Completed
///     cards keep their elapsed after the turn - the chat cards are the record.</summary>
public class ToolElapsedWiringTests
{
  [Fact]
  public async Task Turn_Callbacks_Land_Elapsed_On_Transcript_Cards()
  {
    AgentSessionViewModel vm = TestFixtures.CreateViewModel(
      static (_c, _ct, cb, _n) =>
      {
        cb?.OnToolCall?.Invoke("read", "{}", 1, 1);
        cb?.OnToolResult?.Invoke("read", "ok", "ok", false);
        return Task.FromResult(Result.Success("ack"));
      });

    await vm.SubmitAsync("hi");
    await vm.WaitForTurnAsync();

    ToolCallEntry call = Assert.IsType<ToolCallEntry>(vm.Transcript.Entries.First(e => e is ToolCallEntry));
    ToolResultEntry result = Assert.IsType<ToolResultEntry>(vm.Transcript.Entries.First(e => e is ToolResultEntry));
    Assert.Equal("0.0s", call.ElapsedDisplay);
    Assert.NotEqual("", result.ElapsedDisplay);
  }

  [Fact]
  public async Task Batched_Tools_Each_Carry_Their_Own_Elapsed()
  {
    AgentSessionViewModel vm = TestFixtures.CreateViewModel(
      static (_c, _ct, cb, _n) =>
      {
        cb?.OnToolCall?.Invoke("read", "{}", 1, 2);
        cb?.OnToolResult?.Invoke("read", "ok", "ok", false);
        cb?.OnToolCall?.Invoke("bash", "{}", 2, 2);
        cb?.OnToolResult?.Invoke("bash", "ok", "ok", false);
        return Task.FromResult(Result.Success("done"));
      });

    await vm.SubmitAsync("hi");
    await vm.WaitForTurnAsync();

    ToolCallEntry[] calls = [.. vm.Transcript.Entries.OfType<ToolCallEntry>()];
    ToolResultEntry[] results = [.. vm.Transcript.Entries.OfType<ToolResultEntry>()];
    Assert.Equal(2, calls.Length);
    Assert.Equal(2, results.Length);
    Assert.Equal("read", calls[0].Name);
    Assert.Equal("bash", calls[1].Name);
    Assert.All(calls, call => Assert.Equal("0.0s", call.ElapsedDisplay));
    Assert.All(results, result => Assert.NotEqual("", result.ElapsedDisplay));
  }
}
