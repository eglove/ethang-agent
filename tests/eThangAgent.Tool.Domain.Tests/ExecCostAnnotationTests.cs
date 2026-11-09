using eThangAgent.ToolDomain;

namespace eThangAgent.Tool.Domain.Tests;

public class ExecCostAnnotationTests
{
  [Fact]
  public void Render_Sub_Second_Run_Shows_One_Decimal_And_The_Budget()
      => Assert.Equal("[exec: 0.3s of 600s budget]",
          new ExecCost(TimeSpan.FromMilliseconds(340), TimeSpan.FromSeconds(600)).Render());

  [Fact]
  public void Render_Seconds_And_Above_Shows_Whole_Seconds()
      => Assert.Equal("[exec: 12s of 600s budget]",
          new ExecCost(TimeSpan.FromSeconds(12.4), TimeSpan.FromSeconds(600)).Render());

  [Fact]
  public void IsOverProvisioned_Is_True_Under_A_Tenth_Of_The_Budget()
      => Assert.True(new ExecCost(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(600)).IsOverProvisioned);

  [Fact]
  public void IsOverProvisioned_Is_False_At_Or_Above_A_Tenth_Of_The_Budget()
  {
    Assert.False(new ExecCost(TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(600)).IsOverProvisioned);
    Assert.False(new ExecCost(TimeSpan.FromSeconds(90), TimeSpan.FromSeconds(600)).IsOverProvisioned);
  }

  [Fact]
  public async Task Completed_Run_Carries_The_Cost_Line_And_Never_On_A_Parse_Failure()
  {
    string json = /*lang=json,strict*/ "{\"timeoutSeconds\":600,\"title\":\"t\",\"program\":\"return 1;\"}";
    ExecTool tool = new(new InstantEngine(), ExecOptions.Default, NullExecActivitySink.Instance);

    ToolResult result = await tool.ExecuteAsync(new RawToolInput("exec", json), ct: TestContext.Current.CancellationToken);

    Assert.False(result.IsError);
    Assert.Contains("[exec: ", result.Content, StringComparison.Ordinal);
    Assert.Contains("of 600s budget]", result.Content, StringComparison.Ordinal);
    Assert.NotNull(result.Cost);
  }

  [Fact]
  public async Task Parse_Failure_Carries_No_Cost()
  {
    string json = /*lang=json,strict*/ "{\"timeoutSeconds\":600,\"title\":\"t\",\"program\":\"broken\"}";
    ExecTool tool = new(new ParseFailingEngine(), ExecOptions.Default, NullExecActivitySink.Instance);

    ToolResult result = await tool.ExecuteAsync(new RawToolInput("exec", json), ct: TestContext.Current.CancellationToken);

    Assert.True(result.IsError);
    Assert.Null(result.Cost);
    Assert.DoesNotContain("budget]", result.Content, StringComparison.Ordinal);
  }

  private sealed class InstantEngine : IExecEngine
  {
    public Task<SharedKernel.Result<IReadOnlyList<ExecParseError>>> ValidateAsync(
        ExecProgram program, CancellationToken ct = default) =>
        Task.FromResult(SharedKernel.Result.Success<IReadOnlyList<ExecParseError>>([]));

    public Task<ExecRunResult> ExecuteAsync(ExecProgram program, CancellationToken ct = default) =>
        Task.FromResult(ExecRunResult.Completed("ok"));
  }

  private sealed class ParseFailingEngine : IExecEngine
  {
    public Task<SharedKernel.Result<IReadOnlyList<ExecParseError>>> ValidateAsync(
        ExecProgram program, CancellationToken ct = default) =>
        Task.FromResult(SharedKernel.Result.Success<IReadOnlyList<ExecParseError>>(
            [new ExecParseError(1, 1, "bad")]));

    public Task<ExecRunResult> ExecuteAsync(ExecProgram program, CancellationToken ct = default) =>
        throw new InvalidOperationException("must not execute a program that failed validation");
  }
}
