using System.Threading.Channels;

namespace eThangAgent.Desktop.Streaming;

/// <summary>
/// Bridges agent-loop stream callbacks (arbitrary threads) to a UI sink. Callbacks
/// only write to an unbounded channel; a single reader pumps events to the sink in order.
/// Event-driven — no polling timer. The channel lives for one turn; MarkTurnComplete ends it.
/// The pump AWAITS each sink delivery, so <see cref="DrainUntilIdleAsync"/> means
/// "every event was applied" — which lets production sinks marshal onto the UI thread
/// without losing determinism (<see cref="ViewModels.TranscriptViewModel"/> keeps its
/// UI-thread-only contract). Tests must call <see cref="Start"/> after construction —
/// the pump only runs once started.
/// <para><b>Fault semantics:</b> if the sink throws, the exception is forwarded to every
/// <see cref="DrainUntilIdleAsync"/> awaiter via <c>TrySetException</c> — the task never
/// hangs. Remaining buffered events are abandoned on the first sink fault. The pump task
/// itself stores its <see cref="Task"/> and adds a no-op continuation so the fault is
/// always observed and never raises <see cref="UnobservedTaskException"/>.</para>
/// </summary>
public sealed class StreamBridge(Func<UiStreamEvent, Task> sink)
{
    private readonly Channel<UiStreamEvent> _channel =
        Channel.CreateUnbounded<UiStreamEvent>(new UnboundedChannelOptions { SingleReader = true });
    private readonly TaskCompletionSource _drained = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private StreamBridgePump? _pump;

    public Action<string> OnContentDelta => text => _channel.Writer.TryWrite(new UiStreamEvent.Delta(text));
    public Action<string> OnReasoningDelta => text => _channel.Writer.TryWrite(new UiStreamEvent.Reasoning(text));
    public Action OnIterationEnd => () => _channel.Writer.TryWrite(new UiStreamEvent.IterationEnd());
    public Action<string, string> OnToolCall => (name, args) => _channel.Writer.TryWrite(new UiStreamEvent.ToolCallEvent(name, args));
    public Action<string, string> OnToolResult => (name, summary) => _channel.Writer.TryWrite(new UiStreamEvent.ToolResultEvent(name, summary));

    public void Start()
    {
        _pump = new StreamBridgePump(_channel.Reader, sink, _drained);
        // Store the task and attach a no-op continuation so any unhandled fault
        // is observed; the real fault path goes through _drained.TrySetException.
        Task.Run(_pump.RunAsync).ContinueWith(
            static t => _ = t.Exception, // observe fault
            TaskContinuationOptions.OnlyOnFaulted);
    }

    public void MarkTurnComplete() => _channel.Writer.TryComplete();

    public Task DrainUntilIdleAsync(TimeSpan? pollInterval = null) => _drained.Task;
}

internal sealed class StreamBridgePump(
    ChannelReader<UiStreamEvent> reader, Func<UiStreamEvent, Task> sink, TaskCompletionSource drained)
{
    public async Task RunAsync()
    {
        try
        {
            await foreach (var evt in reader.ReadAllAsync()) await sink(evt);
        }
        catch (Exception ex)
        {
            drained.TrySetException(ex);
            return;
        }
        finally
        {
            // Guard: if we exited via the catch path, TrySetException already settled the
            // TCS; this call is a no-op.  If we exited normally, this is the only setter.
            drained.TrySetResult();
        }
    }
}
