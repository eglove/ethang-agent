using eThangAgent.CapabilityDomain;
using eThangAgent.ToolDomain;

namespace eThangAgent.Roslyn.ACL.Tests;

/// <summary>Exec flow regression (2026-10-04): the engine ran scripts under
///     ExecutionContext.SuppressFlow(), which also suppressed AsyncLocal flow —
///     SubAgentSpawner.RunningChild never reached the script, so a child's own
///     agent.spawn resolved the phantom-root fallback and every grandchild row carried
///     a broken parent id. The engine must suppress the caller's SynchronizationContext
///     (the deadlock it exists to prevent) while KEEPING ExecutionContext flow.
///     The UI-pump suppression stays covered by ScriptThreadingTests.</summary>
public class ScriptExecutionContextFlowTests
{
  private static readonly AsyncLocal<string?> Marker = new();

  private sealed class CapturingProvider : ICapabilityProvider
  {
    public string Id => "capture";

    public IReadOnlyList<ActionDescriptor> Actions { get; } =
    [
        new ActionDescriptor("capture_action", "Capture action.", "Records the ambient marker.", []),
        ];

    public Task<CapabilityInvocationResult> InvokeAsync(
        string actionName, string jsonArguments, CancellationToken ct = default)
    {
      Captured.Add(Marker.Value);
      return Task.FromResult(new CapabilityInvocationResult("ok", false));
    }

    public System.Collections.ObjectModel.Collection<string?> Captured { get; } = [];
  }

  [Fact]
  public async Task ExecScript_AmbientAsyncLocal_FlowsIntoToolInvocations()
  {
    CapturingProvider provider = new();
    CSharpScriptExecEngine engine = new(
        CapabilityRegistry.Create([provider]),
        workspaceRoot: () => AppContext.BaseDirectory);

    Marker.Value = "present";
    ExecRunResult result = await engine.ExecuteAsync(
        new ExecProgram("Tools.Invoke(\"capture_action\", new { timeoutSeconds = 30 })"),
        TestContext.Current.CancellationToken).ConfigureAwait(true);

    Assert.Equal(ExecRunStatus.Completed, result.Status);
    _ = Assert.Single(provider.Captured);
    Assert.Equal("present", provider.Captured[0]);
  }
}
