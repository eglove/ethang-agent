using eThangAgent.CapabilityDomain;

namespace eThangAgent.Roslyn.ACL.Tests;

/// <summary>Universal tool-call budget: ScriptTools REQUIRES timeoutSeconds on every
/// capability invocation, validates it, and strips it before the provider sees the
/// arguments — so providers never reject it as unknown. Pre-dispatch contract
/// violations throw ScriptToolException (see ScriptToolsContractTests). The validated
/// budget bounds EVERY nested action, SelfManaged or not — a hung nested call must
/// not hang the script; on the wire, SelfManaged tools still bound themselves.
/// </summary>
public class ScriptToolsTimeoutTests
{
  private sealed class CapturingProvider : ICapabilityProvider
  {
    public string Id => "cap";
    public string? LastJson { get; private set; }
    public IReadOnlyList<ActionDescriptor> Actions { get; } =
    [
        new ActionDescriptor("do", "Does a thing.",
                "Contract text.", [new ActionParameter("x", "String", "Some value.")]),
        ];
    public Task<CapabilityInvocationResult> InvokeAsync(string actionName,
        string jsonArguments, CancellationToken ct = default)
    {
      LastJson = jsonArguments;
      return Task.FromResult(CapabilityInvocationResult.Ok("done"));
    }
  }

  private static (ScriptTools Tools, CapturingProvider Provider) Make()
  {
    CapturingProvider provider = new();
    CapabilityRegistry registry = CapabilityRegistry.Create([provider]);
    ScriptGlobals globals = new(registry, ".", Path.GetTempPath());
    return (globals.Tools, provider);
  }

  [Fact]
  public void MissingTimeout_ThrowsLoudly()
  {
    (ScriptTools? tools, CapturingProvider _) = Make();
    Exception ex = Assert.Throws<ScriptToolException>(() => tools.Invoke("do", new { x = "y" }));
    Assert.Contains("Error [MissingParameter]:", ex.Message, StringComparison.Ordinal);
    Assert.Contains("timeoutSeconds", ex.Message, StringComparison.Ordinal);
  }

  [Theory]
  [InlineData(0)]
  [InlineData(-5)]
  [InlineData(3601)]
  public void OutOfRangeTimeout_ThrowsLoudly(int seconds)
  {
    (ScriptTools? tools, CapturingProvider _) = Make();
    Exception ex = Assert.Throws<ScriptToolException>(() => tools.Invoke("do", new { x = "y", timeoutSeconds = seconds }));
    Assert.Contains("Error [InvalidParameterValue]:", ex.Message, StringComparison.Ordinal);
  }

  [Fact]
  public void ValidTimeout_ReachesProvider_StrippedFromArguments()
  {
    (ScriptTools? tools, CapturingProvider? provider) = Make();
    string result = tools.Invoke("do", new { x = "y", timeoutSeconds = 30 });
    Assert.Equal("done", result);
    Assert.NotNull(provider.LastJson);
    Assert.DoesNotContain("timeoutSeconds", provider.LastJson, StringComparison.Ordinal);
    Assert.Contains("\"x\"", provider.LastJson, StringComparison.Ordinal);
  }

  [Fact]
  public async Task ElapsedTimeout_SurfacesAsToolTimeout()
  {
    SlowProvider slow = new();
    CapabilityRegistry registry = CapabilityRegistry.Create([slow]);
    ScriptGlobals globals = new(registry, ".", Path.GetTempPath());

    string result = await Task.Run(() => globals.Tools.Invoke("slow", new { timeoutSeconds = 1 })).ConfigureAwait(true);

    Assert.Contains("Error [ToolTimeout]:", result, StringComparison.Ordinal);
  }

  [Fact]
  public void StringArguments_Form_ThrowsLoudly()
  {
    (ScriptTools? tools, CapturingProvider _) = Make();
    Exception ex = Assert.Throws<ScriptToolException>(() => tools.Invoke("do", /*lang=json,strict*/ """{"x":"y"}"""));
    Assert.Contains("Error [MissingParameter]:", ex.Message, StringComparison.Ordinal);
  }

  [Fact]
  public async Task SelfManagedAction_BoundedByNestedBudget()
  {
    DelayingProvider selfManaged = new(TimeoutPolicy.SelfManaged);
    ScriptGlobals globals = new(
        CapabilityRegistry.Create([selfManaged]), ".", Path.GetTempPath());

    // The stated nested budget bounds EVERY action: a hung nested call must not hang
    // the script. SelfManaged's remaining meaning is contract-level (the tool
    // re-validates its own envelope on the wire), not an exemption from the budget.
    string result = await Task.Run(() => globals.Tools.Invoke("waiter", new { timeoutSeconds = 1 })).ConfigureAwait(true);

    Assert.StartsWith("Error [ToolTimeout]", result, StringComparison.Ordinal);
  }

  [Fact]
  public void SelfManagedAction_StillRequiresValidBudget()
  {
    DelayingProvider selfManaged = new(TimeoutPolicy.SelfManaged);
    ScriptGlobals globals = new(
        CapabilityRegistry.Create([selfManaged]), ".", Path.GetTempPath());

    Exception ex = Assert.Throws<ScriptToolException>(() => globals.Tools.Invoke("waiter", new { }));
    Assert.Contains("Error [MissingParameter]:", ex.Message, StringComparison.Ordinal);
  }

  /// <summary>Completes after 1.2s — beyond any 1-second budget.</summary>
  private sealed class DelayingProvider(TimeoutPolicy policy) : ICapabilityProvider
  {
    public string Id => "delayer";
    public IReadOnlyList<ActionDescriptor> Actions { get; } =
    [
        new ActionDescriptor("waiter", "Waits.", "Outlives its budget.",
                [], RequiredParameters: null, Timeout: policy),
        ];
    public async Task<CapabilityInvocationResult> InvokeAsync(string actionName,
        string jsonArguments, CancellationToken ct = default)
    {
      await Task.Delay(1_200, CancellationToken.None).ConfigureAwait(false);
      return CapabilityInvocationResult.Ok("late-but-done");
    }
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
