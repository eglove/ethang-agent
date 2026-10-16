
using eThangAgent.SharedKernel;
using eThangAgent.ToolDomain;

namespace eThangAgent.Tool.Domain.Tests;

/// <summary>Issue #124 defect 2 (2026-10-15 incident): the exec envelope's title and
///     timeoutSeconds are TRANSPORT metadata — the model varied the title on every
///     call and the repeat guard's identical-args key reset every call, so one
///     identical successful call ran 195 times. The tool must expose its SEMANTIC
///     call identity (the program body) separately from the raw envelope.</summary>
public class ExecToolCallIdentityTests
{
  [Fact]
  public void ExecTool_DeclaresTheCallIdentitySeam()
    // The loop normalizes guard keys only for tools declaring IToolCallIdentifiable:
    // the real exec tool must declare it, or envelope variation defeats the guard again.
    => Assert.True(CreateTool() is IToolCallIdentifiable);

  private static ExecTool CreateTool()
      => new(new StubEngine(), ExecOptions.Default, NullExecActivitySink.Instance);

  private sealed class StubEngine : IExecEngine
  {
    public Task<Result<IReadOnlyList<ExecParseError>>> ValidateAsync(ExecProgram program, CancellationToken ct = default)
        => Task.FromResult(Result.Success<IReadOnlyList<ExecParseError>>([]));

    public Task<ExecRunResult> ExecuteAsync(ExecProgram program, CancellationToken ct = default)
        => Task.FromResult(ExecRunResult.Completed(""));
  }

  [Fact]
  public void CallIdentity_TwoCalls_DifferentTitles_SameProgram_Identical()
  {
    string argumentsOne = System.Text.Json.JsonSerializer.Serialize(new
    {
      timeoutSeconds = 120,
      title = "dump Task 1 test verbatim one hundred seventy-nine",
      program = "return 42;",
    });
    string argumentsTwo = System.Text.Json.JsonSerializer.Serialize(new
    {
      timeoutSeconds = 120,
      title = "dump Task 1 test verbatim one hundred eighty-four",
      program = "return 42;",
    });

    string? identityOne = ExecTool.CallIdentity(argumentsOne);
    string? identityTwo = ExecTool.CallIdentity(argumentsTwo);

    Assert.NotNull(identityOne);
    Assert.Equal(identityOne, identityTwo);
  }

  [Fact]
  public void CallIdentity_DifferentPrograms_Differ()
  {
    string argumentsOne = System.Text.Json.JsonSerializer.Serialize(new
    {
      timeoutSeconds = 120,
      title = "t",
      program = "return 42;",
    });
    string argumentsTwo = System.Text.Json.JsonSerializer.Serialize(new
    {
      timeoutSeconds = 120,
      title = "t",
      program = "return 43;",
    });

    Assert.NotEqual(ExecTool.CallIdentity(argumentsOne), ExecTool.CallIdentity(argumentsTwo));
  }

  [Fact]
  public void CallIdentity_MalformedJson_ReturnsNull_SoTheGuardFallsBackToRawArguments()
    => Assert.Null(ExecTool.CallIdentity("not json"));


}
