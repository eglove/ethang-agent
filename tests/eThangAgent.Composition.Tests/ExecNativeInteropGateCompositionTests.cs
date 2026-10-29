using eThangAgent.AgentDomain;
using eThangAgent.ModelDomain;
using eThangAgent.Roslyn.ACL;
using eThangAgent.SharedKernel;
using eThangAgent.ToolDomain;
using Microsoft.Extensions.DependencyInjection;

namespace eThangAgent.Composition.Tests;

/// <summary>Issue #282, the session-level half of the acceptance criteria: a session with
///     <c>computer_use_enabled</c> OFF must not be able to move the cursor or foreground-switch
///     the user's desktop through ANY tool surface — <c>exec</c> included.
///     <para>The existing <see cref="ComputerUseCompositionTests"/> matrix proves the
///     <c>computer</c> tool is absent from every surface when the flag is off. That was never
///     the whole claim: the defect was that <c>exec</c> reached the same native surface with no
///     gate at all, so a disabled computer tool meant nothing. These tests close that half by
///     driving the REAL container's exec engine — not a hand-built one — because the wiring is
///     exactly what could regress.</para>
///     <para>Note the gate is deliberately UNCONDITIONAL: it does not read
///     <c>settings.ComputerUse</c>. Native interop from exec is refused whether computer use is
///     enabled or disabled, because the sanctioned path is the broker in both cases — an enabled
///     computer tool is not a licence to bypass the containment, audit trail, and focus policy
///     the broker exists to supply.</para></summary>
public class ExecNativeInteropGateCompositionTests
{
  private static AgentSettings Settings(bool computerUse) => new(
      new OpenRouterSettings("sk-or-test", new Uri("https://openrouter.test")),
      new SubAgentOptions(null, 2),
      ComputerUse: computerUse);

  private static ServiceProvider Build(bool computerUse) =>
      new ServiceCollection()
          .AddEThangAgentCore(Settings(computerUse), Providers.OpenRouter,
              ModelConfig.Create("test/model", 512, 0.5f, 8192).Value!,
              new AgentHostOptions(
                  FixedWorkspaceContext.ForPath("ws"), new UnrootedPathResolver()))
          .BuildServiceProvider();

  /// <summary>The issue's exact reproduction: a script that declares user32 externs and
  ///     calls them. Validated through the container's own engine, so a wiring change that
  ///     dropped the gate fails here.</summary>
  private const string CursorMovingProgram = """
      using System.Runtime.InteropServices;
      [DllImport("user32.dll")]
      static extern bool SetCursorPos(int x, int y);
      [DllImport("user32.dll")]
      static extern bool SetForegroundWindow(IntPtr hWnd);
      SetCursorPos(0, 0);
      return SetForegroundWindow(IntPtr.Zero);
      """;

  /// <summary>Validates through the container's engine and returns the violations. A gate
  ///     failure is a VALIDATION failure — the program never runs.</summary>
  private static async Task<IReadOnlyList<ExecParseError>> ViolationsAsync(ServiceProvider services, string program)
  {
    IExecEngine engine = services.GetRequiredService<IExecEngine>();
    Result<IReadOnlyList<ExecParseError>> result = await engine
        .ValidateAsync(new ExecProgram(program), TestContext.Current.CancellationToken)
        .ConfigureAwait(false);
    Assert.True(result.IsSuccess, "validation must not fault");
    return result.Value;
  }

  [Fact]
  public async Task ComputerUseDisabled_ExecRefusesCursorAndForegroundControl()
  {
    using ServiceProvider services = Build(computerUse: false);
    IReadOnlyList<ExecParseError> violations =
        await ViolationsAsync(services, CursorMovingProgram).ConfigureAwait(true);

    Assert.NotEmpty(violations);
    Assert.Contains(violations, e => e.Message.Contains(NativeInteropPolicy.ErrorCode, StringComparison.Ordinal));
  }

  [Fact]
  public async Task ComputerUseDisabled_ExecRefusalNamesTheComputerTool()
  {
    // The redirect matters as much as the refusal: a model told only 'no' retries with a
    // different spelling. The message must name the sanctioned path.
    using ServiceProvider services = Build(computerUse: false);
    IReadOnlyList<ExecParseError> violations =
        await ViolationsAsync(services, CursorMovingProgram).ConfigureAwait(true);

    Assert.Contains(violations, e => e.Message.Contains("computer", StringComparison.Ordinal));
  }

  [Fact]
  public async Task ComputerUseEnabled_ExecStillRefusesNativeInterop()
  {
    // Enabling the computer tool must NOT open exec's native reach. The broker is the
    // sanctioned path in both states; the flag chooses whether the tool exists, never
    // whether the containment can be bypassed.
    using ServiceProvider services = Build(computerUse: true);
    IReadOnlyList<ExecParseError> violations =
        await ViolationsAsync(services, CursorMovingProgram).ConfigureAwait(true);

    Assert.NotEmpty(violations);
    Assert.Contains(violations, e => e.Message.Contains(NativeInteropPolicy.ErrorCode, StringComparison.Ordinal));
  }

  [Fact]
  public async Task ComputerUseDisabled_OrdinaryExecStillRuns()
  {
    // The gate must not be a blanket refusal of exec: an ordinary program validates clean
    // and runs, or the fix has broken the tool it was protecting.
    using ServiceProvider services = Build(computerUse: false);
    IReadOnlyList<ExecParseError> validation =
        await ViolationsAsync(services, "return 1 + 1;").ConfigureAwait(true);
    Assert.Empty(validation);

    IExecEngine engine = services.GetRequiredService<IExecEngine>();
    ExecRunResult run = await engine
        .ExecuteAsync(new ExecProgram("return 1 + 1;"), TestContext.Current.CancellationToken)
        .ConfigureAwait(true);
    Assert.Equal(ExecRunStatus.Completed, run.Status);
    Assert.Equal("2", run.Output);
  }
}
