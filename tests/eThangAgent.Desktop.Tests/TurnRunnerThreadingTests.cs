using eThangAgent.Agent.Application;
using eThangAgent.Desktop;
using eThangAgent.Desktop.ViewModels;
using eThangAgent.SharedKernel;

/// <summary>Turns must execute on the worker pool without flowing the caller's context:
///     a turn started on the Avalonia UI thread would pin every continuation to its pump,
///     and one sync-blocking tool deadlocks the app (production freeze regression).</summary>
public class TurnRunnerThreadingTests
{
    [Fact]
    public async Task OffUiThread_RunsHandler_WithoutLeakingCallerContext()
    {
        var leaked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        SynchronizationContext? observed = null;
        Result<string> result = null!;

        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(
            new RecordingContext(() => leaked.TrySetResult()));
        try
        {
            // Start under the hostile context; do NOT await here — the test's own
            // resumption must not be subject to the context under test.
            var task = DesktopHost.OffUiThread((_, _, _, _, _, _, _) =>
            {
                observed = SynchronizationContext.Current;
                return Task.FromResult(Result<string>.Success("done"));
            })(new SendMessageCommand("hi"), CancellationToken.None,
               null, null, null, null, null);

            SynchronizationContext.SetSynchronizationContext(previous);
            result = await task.WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
        }

        Assert.False(leaked.Task.IsCompleted, "turn posted back onto the caller's context");
        Assert.True(result.IsSuccess);
        Assert.Equal("done", result.Value);
        Assert.Null(observed);
    }

    private sealed class RecordingContext(Action onPost) : SynchronizationContext
    {
        public override void Post(SendOrPostCallback d, object? state) => onPost();

        public override void Send(SendOrPostCallback d, object? state) => onPost();
    }
}
