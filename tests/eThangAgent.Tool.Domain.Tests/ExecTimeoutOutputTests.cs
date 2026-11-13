using eThangAgent.SharedKernel;

namespace eThangAgent.ToolDomain.Tests;

/// <summary>An elapsed budget abandons the program mid-flight. What it had already printed
/// is the only account of how far it got, so it rides the timeout result instead of being
/// lost with the run that never completed.</summary>
public class ExecTimeoutOutputTests
{
  [Fact]
  public void Progress_Renders_Lines_In_Order()
  {
    ExecOutputProgress progress = new();
    progress.Add("line1");
    progress.Add("line2");

    Assert.Equal("line1\nline2", progress.Render());
  }

  [Fact]
  public void Progress_Renders_Empty_When_Nothing_Printed() => Assert.Equal(string.Empty, new ExecOutputProgress().Render());

  [Fact]
  public void Progress_Keeps_The_Tail_And_Names_What_It_Omitted()
  {
    ExecOutputProgress progress = new();
    progress.Add(new string('x', ExecOutputProgress.MaxChars + 500));

    string rendered = progress.Render();

    Assert.Contains("500 earlier character(s) of output omitted", rendered, StringComparison.Ordinal);
    Assert.EndsWith(new string('x', ExecOutputProgress.MaxChars), rendered, StringComparison.Ordinal);
  }

  [Fact]
  public void TimedOut_Carries_What_The_Run_Printed()
  {
    ExecOutputProgress progress = new();
    progress.Add("compiling...");
    progress.Add("running tests...");

    ToolResult result = ToolTimeout.TimedOut("exec", TimeSpan.FromSeconds(600), progress);

    Assert.True(result.IsError);
    Assert.StartsWith("Error [ToolTimeout]:", result.Content, StringComparison.Ordinal);
    Assert.Contains("compiling...", result.Content, StringComparison.Ordinal);
    Assert.Contains("running tests...", result.Content, StringComparison.Ordinal);
    Assert.Contains("abandoned mid-flight", result.Content, StringComparison.Ordinal);
  }

  [Fact]
  public void TimedOut_Without_Printed_Output_Is_The_Plain_Message()
  {
    ToolResult result = ToolTimeout.TimedOut("exec", TimeSpan.FromSeconds(600), new ExecOutputProgress());

    Assert.Equal(ToolTimeout.TimedOut("exec", TimeSpan.FromSeconds(600)).Content, result.Content);
  }

  [Fact]
  public void TimedOut_With_No_Record_Is_The_Plain_Message()
  {
    ToolResult result = ToolTimeout.TimedOut("read", TimeSpan.FromSeconds(45), progress: null);

    Assert.Equal(ToolTimeout.TimedOut("read", TimeSpan.FromSeconds(45)).Content, result.Content);
  }

  [Fact]
  public async Task RunAsync_Reports_Progress_When_The_Budget_Elapses()
  {
    ExecOutputProgress progress = new();
    progress.Add("halfway there");

    ToolResult result = await ToolExecution.RunAsync(
        "exec", TimeSpan.FromSeconds(1),
        async token =>
        {
          await Task.Delay(Timeout.Infinite, token).ConfigureAwait(false);
          return new ToolResult("unreachable", false);
        },
        progress, TestContext.Current.CancellationToken);

    Assert.True(result.IsError);
    Assert.Contains("Error [ToolTimeout]: 'exec' exceeded the 1s execution budget", result.Content, StringComparison.Ordinal);
    Assert.Contains("halfway there", result.Content, StringComparison.Ordinal);
  }

  /// <summary>The whole path, as the model meets it: one exec call whose program prints and
  /// then overruns its budget. The printed lines come back in the timeout result, so the
  /// caller learns how far the run got instead of only that it stopped.</summary>
  [Fact]
  public async Task ExecCall_That_Overruns_Returns_What_It_Printed()
  {
    ExecTool tool = new(new PrintingHungEngine(), ExecOptions.Default, NullExecActivitySink.Instance);

    ToolResult result = await tool.ExecuteAsync(
        new RawToolInput("exec", /*lang=json,strict*/ "{\"timeoutSeconds\":1,\"title\":\"probe\",\"program\":\"x\"}"),
        ct: TestContext.Current.CancellationToken);

    Assert.True(result.IsError);
    Assert.Contains("Error [ToolTimeout]:", result.Content, StringComparison.Ordinal);
    Assert.Contains("printed before the budget elapsed", result.Content, StringComparison.Ordinal);
    Assert.Contains("compiling...", result.Content, StringComparison.Ordinal);
  }

  /// <summary>Prints into the run's record and then never finishes, so the budget is what
  /// ends it.</summary>
  private sealed class PrintingHungEngine : IExecEngine
  {
    public Task<Result<IReadOnlyList<ExecParseError>>> ValidateAsync(
        ExecProgram program, CancellationToken ct = default)
        => Task.FromResult(Result.Success<IReadOnlyList<ExecParseError>>([]));

    public async Task<ExecRunResult> ExecuteAsync(ExecProgram program, CancellationToken ct = default)
    {
      Assert.NotNull(program.Progress);
      program.Progress.Add("compiling...");
      await Task.Delay(Timeout.Infinite, ct).ConfigureAwait(false);
      return ExecRunResult.Completed(string.Empty);
    }
  }
}
