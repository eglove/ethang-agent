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
