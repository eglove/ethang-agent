using eThangAgent.CapabilityDomain;

namespace eThangAgent.Roslyn.ACL.Tests;

/// <summary>Nested-tool-call contract: ScriptTools Invoke must fail LOUD (thrown
///     ScriptToolException) when a script violates a pre-dispatch contract — missing or
///     invalid timeoutSeconds, unknown action, malformed arguments — because batch
///     scripts routinely discard result strings, and an in-band error text silently
///     buried among successes is how a five-Add todo batch lost every item without a
///     trace. Post-dispatch outcomes (tool-level errors, elapsed budgets) stay in-band:
///     those are ordinary tool results the script is expected to read and branch on.</summary>
public class ScriptToolsContractTests
{
  private sealed class StubProvider : ICapabilityProvider
  {
    public string Id => "stub";
    public IReadOnlyList<ActionDescriptor> Actions { get; } =
    [
        new ActionDescriptor("do", "Does a thing.", "Contract text.",
            [new ActionParameter("x", "String", "Some value.")]),
    ];
    public Task<CapabilityInvocationResult> InvokeAsync(string actionName,
        string jsonArguments, CancellationToken ct = default) =>
        Task.FromResult(CapabilityInvocationResult.Fail("Error [ToolError]: tool-level failure"));
  }

  private static ScriptTools Make()
  {
    CapabilityRegistry registry = CapabilityRegistry.Create([new StubProvider()]);
    ScriptGlobals globals = new(registry, ".", Path.GetTempPath());
    return globals.Tools;
  }

  // --- Pre-dispatch contract violations throw loudly ---

  [Fact]
  public void MissingTimeout_Throws()
  {
    ScriptTools tools = Make();
    Exception ex = Assert.Throws<ScriptToolException>(() => tools.Invoke("do", new { x = "y" }));
    Assert.Contains("Error [MissingParameter]", ex.Message, StringComparison.Ordinal);
  }

  [Theory]
  [InlineData(0)]
  [InlineData(-5)]
  [InlineData(3601)]
  public void OutOfRangeTimeout_Throws(int seconds)
  {
    ScriptTools tools = Make();
    Exception ex = Assert.Throws<ScriptToolException>(() => tools.Invoke("do", new { x = "y", timeoutSeconds = seconds }));
    Assert.Contains("Error [InvalidParameterValue]", ex.Message, StringComparison.Ordinal);
  }

  [Fact]
  public void UnknownAction_Throws()
  {
    ScriptTools tools = Make();
    Exception ex = Assert.Throws<ScriptToolException>(() => tools.Invoke("nope", new { timeoutSeconds = 30 }));
    Assert.Contains("Error [UnknownAction]", ex.Message, StringComparison.Ordinal);
  }

  [Fact]
  public void MalformedJsonArguments_Throw()
  {
    ScriptTools tools = Make();
    Exception ex = Assert.Throws<ScriptToolException>(() => tools.Invoke("do", "{ not json"));
    Assert.Contains("Error [InvalidJsonArguments]", ex.Message, StringComparison.Ordinal);
  }

  [Fact]
  public void NonObjectArguments_Throw()
  {
    ScriptTools tools = Make();
    Exception ex = Assert.Throws<ScriptToolException>(() => tools.Invoke("do", "[1,2]"));
    Assert.Contains("Error [InvalidJsonArguments]", ex.Message, StringComparison.Ordinal);
  }

  // --- Post-dispatch outcomes stay in-band strings the script reads ---

  [Fact]
  public void ToolLevelError_StaysInBand()
  {
    ScriptTools tools = Make();
    string result = tools.Invoke("do", new { timeoutSeconds = 30, x = "y" });
    Assert.StartsWith("Error [ToolError]", result, StringComparison.Ordinal);
  }

  [Fact]
  public async Task ElapsedBudget_SurfacesInBand()
  {
    SlowProvider slow = new();
    ScriptGlobals globals = new(CapabilityRegistry.Create([slow]), ".", Path.GetTempPath());
    string result = await Task.Run(() => globals.Tools.Invoke("slow", new { timeoutSeconds = 1 })).ConfigureAwait(true);
    Assert.StartsWith("Error [ToolTimeout]", result, StringComparison.Ordinal);
  }

  // --- Argument-shape contract errors throw loudly (research 2026-08-30):
  //     codes knowable from the arguments alone have no legitimate continue-path;
  //     buried among successes they silently lose work (corpus: 53 + 25 occurrences).

  [Theory]
  [InlineData("InvalidParameterValue")]
  [InlineData("MissingParameter")]
  public void ContractErrorCodes_Throw_Loudly(string code)
  {
    ScriptGlobals globals = new(CapabilityRegistry.Create([new CodedErrorProvider(code)]), ".", Path.GetTempPath());
    Exception ex = Assert.Throws<ScriptToolException>(() => globals.Tools.Invoke("boom", new { timeoutSeconds = 30 }));
    Assert.Contains($"Error [{code}]", ex.Message, StringComparison.Ordinal);
  }

  [Theory]
  [InlineData("AnchorNotFound")]
  [InlineData("DirectoryNotFound")]
  [InlineData("FileNotFound")]
  [InlineData("PathOutsideWorkspace")]
  [InlineData("ToolError")]
  public void EnvironmentalOutcomes_StayInBand(string code)
  {
    ScriptGlobals globals = new(CapabilityRegistry.Create([new CodedErrorProvider(code)]), ".", Path.GetTempPath());
    string result = globals.Tools.Invoke("boom", new { timeoutSeconds = 30 });
    Assert.StartsWith($"Error [{code}]", result, StringComparison.Ordinal);
  }

  [Fact]
  public void Require_Throws_On_Error_Result()
  {
    Exception ex = Assert.Throws<ScriptToolException>(() => ScriptTools.Require("Error [AnchorNotFound]: nope"));
    Assert.Contains("Error [AnchorNotFound]", ex.Message, StringComparison.Ordinal);
  }

  [Fact]
  public void Require_Returns_The_Result_On_Success() =>
      Assert.Equal("[edit a.cs] replaced 1 occurrence", ScriptTools.Require("[edit a.cs] replaced 1 occurrence"));

  private sealed class CodedErrorProvider(string code) : ICapabilityProvider
  {
    public string Id => "coded";
    public IReadOnlyList<ActionDescriptor> Actions { get; } =
    [
        new ActionDescriptor("boom", "Fails with a fixed code.", "Test stub.", []),
    ];
    public Task<CapabilityInvocationResult> InvokeAsync(string actionName,
        string jsonArguments, CancellationToken ct = default) =>
        Task.FromResult(CapabilityInvocationResult.Fail($"Error [{code}]: boom failed"));
  }

  private sealed class SlowProvider : ICapabilityProvider
  {
    public string Id => "slowp";
    public IReadOnlyList<ActionDescriptor> Actions { get; } =
    [
        new ActionDescriptor("slow", "Takes forever.", "Never returns in time.", []),
    ];
    public async Task<CapabilityInvocationResult> InvokeAsync(string actionName,
        string jsonArguments, CancellationToken ct = default)
    {
      await Task.Delay(Timeout.InfiniteTimeSpan, ct).ConfigureAwait(false);
      return CapabilityInvocationResult.Ok("never");
    }
  }
}
