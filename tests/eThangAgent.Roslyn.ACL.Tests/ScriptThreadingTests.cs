using eThangAgent.CapabilityDomain;
using eThangAgent.ToolDomain;

namespace eThangAgent.Roslyn.ACL.Tests;

/// <summary>Scripts and tool invocations are synchronous model-authored code; they must
///     complete without ever posting back to the caller's SynchronizationContext. The
///     ACLs shed the ambient context at their boundaries so a blocked pump can never
///     deadlock a turn. Regression for a production freeze: the whole agent turn ran on
///     Avalonia's UI thread and Tools.Invoke blocked it inline.</summary>
public class ScriptThreadingTests
{
    [Fact]
    public async Task ExecScript_CallingToolsInvoke_Completes_UnderNonPumpingContext()
    {
        var engine = new CSharpScriptExecEngine(
            CapabilityRegistry.Create([new StubProvider()]), ExecOptions.Default);

        var outcome = await RunUnderNonPumpingContext(
            () => engine.ExecuteAsync(new ExecProgram("Tools.Invoke(\"stub_action\", new { timeoutSeconds = 30 })")));

        Assert.False(outcome.Leaked, "execution posted back onto the caller's context");
        Assert.Equal(ExecRunStatus.Completed, outcome.Value.Status);
        Assert.Contains("ok", outcome.Value.Output);
    }

    [Fact]
    public async Task ScriptTools_Invoke_Completes_UnderNonPumpingContext()
    {
        var globals = new ScriptGlobals(CapabilityRegistry.Create([new StubProvider()]),
            workspace: ".", temp: Path.GetTempPath());

        var outcome = await RunUnderNonPumpingContext(
            () => Task.FromResult(globals.Tools.Invoke("stub_action", new { timeoutSeconds = 30 })));

        Assert.False(outcome.Leaked, "invocation posted back onto the caller's context");
        Assert.Equal("ok", outcome.Value);
    }

    /// <summary>Runs the work on a worker thread with a non-pumping context installed for
    ///     its duration. Any continuation posted onto that context marks the run as
    ///     leaked — a fail-fast signal instead of a hung test. The watchdog bounds even a
    ///     true deadlock.</summary>
    private static async Task<(T Value, bool Leaked)> RunUnderNonPumpingContext<T>(
        Func<Task<T>> work)
    {
        var leaked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        Task<T> task;
        try
        {
            // Install inside the worker body: every await the work captures from here on
            // would post back to this context if any leaked through.
            task = Task.Run(() =>
            {
                var previous = SynchronizationContext.Current;
                SynchronizationContext.SetSynchronizationContext(
                    new NonPumpingContext(() => leaked.TrySetResult()));
                try { return work(); }
                finally { SynchronizationContext.SetSynchronizationContext(previous); }
            });
        }
        finally { }

        var completed = await Task.WhenAny(task, leaked.Task,
            Task.Delay(TimeSpan.FromSeconds(30)));

        if (completed == leaked.Task)
            return (default!, true);
        Assert.True(completed == task, "work neither completed nor leaked within 30s");
        return (await task, false);
    }

    private sealed class NonPumpingContext(Action onLeak) : SynchronizationContext
    {
        public override void Post(SendOrPostCallback d, object? state) => onLeak();

        public override void Send(SendOrPostCallback d, object? state) => onLeak();
    }

    /// <summary>Completes asynchronously so a continuation genuinely has to be scheduled
    ///     somewhere — a synchronously-completing stub could hide a missing shed.</summary>
    private sealed class StubProvider : ICapabilityProvider
    {
        public string Id => "stub";

        public IReadOnlyList<ActionDescriptor> Actions { get; } =
        [
            new ActionDescriptor("stub_action", "Stub action.", "Always returns ok.", []),
        ];

        public async Task<CapabilityInvocationResult> InvokeAsync(
            string actionName, string jsonArguments, CancellationToken ct = default)
        {
            await Task.Yield();
            return CapabilityInvocationResult.Ok("ok");
        }
    }
}
