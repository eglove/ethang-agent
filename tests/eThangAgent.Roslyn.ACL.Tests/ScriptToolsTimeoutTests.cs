using eThangAgent.CapabilityDomain;

namespace eThangAgent.Roslyn.ACL.Tests;

/// <summary>Universal tool-call budget: ScriptTools REQUIRES timeoutSeconds on every
/// capability invocation, validates it, and strips it before the provider sees the
/// arguments — so providers never reject it as unknown. Enforcement follows the action's
/// TimeoutPolicy: HarnessEnforced actions are cancelled on elapsed budgets; SelfManaged
/// actions (the ITool-backed agent tools, incl. deliberately unbounded clarify) never are.</summary>
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
  public void MissingTimeout_FailsWithMissingParameter()
  {
    (ScriptTools? tools, CapturingProvider _) = Make();
    string result = tools.Invoke("do", new { x = "y" });
    Assert.Contains("Error [MissingParameter]:", result);
    Assert.Contains("timeoutSeconds", result);
  }

  [Theory]
  [InlineData(0)]
  [InlineData(-5)]
  [InlineData(3601)]
  public void OutOfRangeTimeout_Fails(int seconds)
  {
    (ScriptTools? tools, CapturingProvider _) = Make();
    string result = tools.Invoke("do", new { x = "y", timeoutSeconds = seconds });
    Assert.Contains("Error [InvalidParameterValue]:", result);
  }

  [Fact]
  public void ValidTimeout_ReachesProvider_StrippedFromArguments()
  {
    (ScriptTools? tools, CapturingProvider? provider) = Make();
    string result = tools.Invoke("do", new { x = "y", timeoutSeconds = 30 });
    Assert.Equal("done", result);
    Assert.NotNull(provider.LastJson);
    Assert.DoesNotContain("timeoutSeconds", provider.LastJson);
    Assert.Contains("\"x\"", provider.LastJson);
  }

  [Fact]
  public async Task ElapsedTimeout_SurfacesAsToolTimeout()
  {
    SlowProvider slow = new();
    CapabilityRegistry registry = CapabilityRegistry.Create([slow]);
    ScriptGlobals globals = new(registry, ".", Path.GetTempPath());

    string result = await Task.Run(() => globals.Tools.Invoke("slow", new { timeoutSeconds = 1 }));

    Assert.Contains("Error [ToolTimeout]:", result);
  }

  [Fact]
  public void StringArguments_Form_EnforcesTimeoutToo()
  {
    (ScriptTools? tools, CapturingProvider _) = Make();
    string result = tools.Invoke("do", /*lang=json,strict*/ """{"x":"y"}""");
    Assert.Contains("Error [MissingParameter]:", result);
  }

  [Fact]
  public async Task SelfManagedAction_BeyondBudget_StillCompletes_NoToolTimeout()
  {
    DelayingProvider selfManaged = new(TimeoutPolicy.SelfManaged);
    ScriptGlobals globals = new(
        CapabilityRegistry.Create([selfManaged]), ".", Path.GetTempPath());

    // Budget elapses while the action runs — a HarnessEnforced action dies here;
    // a SelfManaged action (clarify waiting on the human) completes regardless.
    string result = await Task.Run(() => globals.Tools.Invoke("waiter", new { timeoutSeconds = 1 }));

    Assert.Equal("late-but-done", result);
  }

  [Fact]
  public void SelfManagedAction_StillRequiresValidBudget()
  {
    DelayingProvider selfManaged = new(TimeoutPolicy.SelfManaged);
    ScriptGlobals globals = new(
        CapabilityRegistry.Create([selfManaged]), ".", Path.GetTempPath());

    string missing = globals.Tools.Invoke("waiter", new { });
    Assert.Contains("Error [MissingParameter]:", missing);
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
      await Task.Delay(1_200, CancellationToken.None);
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
      await Task.Delay(Timeout.InfiniteTimeSpan, ct);
      return CapabilityInvocationResult.Ok("never");
    }
  }
}
