using eThangAgent.SharedKernel;

namespace eThangAgent.ToolDomain.Tests;

/// <summary>Pins the timeout-authority contract: the per-call timeoutSeconds argument is
/// the ONLY execution budget for exec - ExecOptions carries no baked-in default, and the
/// advertised docs say so verbatim.</summary>
public class ExecTimeoutAuthorityTests
{
  [Fact]
  public void ExecOptions_HasNoTimeoutProperty() =>
    // A default here would silently override or duplicate the model-supplied budget.
    Assert.Null(typeof(ExecOptions).GetProperty("Timeout"));

  [Fact]
  public void Guide_TimeoutSection_NamesPerCallBudgetAsSoleBudget_NoHardCap()
  {
    Assert.DoesNotContain("hard cap", ExecGuide.Text, StringComparison.Ordinal);
    Assert.Contains("timeoutSeconds is the only execution budget", ExecGuide.Text, StringComparison.Ordinal);
    Assert.Contains("Error [ToolTimeout]", ExecGuide.Text, StringComparison.Ordinal);
  }

  [Fact]
  public void ToolDescription_NamesPerCallBudgetAsSoleBudget_NoHardCap()
  {
    string description = new ExecTool(
        new StubExecEngine(), ExecOptions.Default,
        new StubOutputStore(), NullExecActivitySink.Instance).Definition.Description;

    Assert.DoesNotContain("hard cap", description, StringComparison.Ordinal);
    Assert.Contains("timeoutSeconds is the only execution budget", description, StringComparison.Ordinal);
  }

  [Fact]
  public void Guide_VersionBumped() => Assert.Equal("2.4", ExecGuide.Version);

  private sealed class StubExecEngine : IExecEngine
  {
    public Task<Result<IReadOnlyList<ExecParseError>>> ValidateAsync(ExecProgram program, CancellationToken ct = default)
        => Task.FromResult(Result.Success<IReadOnlyList<ExecParseError>>([]));
    public Task<ExecRunResult> ExecuteAsync(ExecProgram program, CancellationToken ct = default)
        => Task.FromResult(new ExecRunResult(ExecRunStatus.Completed, "", []));
  }

  private sealed class StubOutputStore : IExecOutputStore
  {
    public Task<string> WriteAsync(string content, CancellationToken ct = default)
        => Task.FromResult("");
  }
}
