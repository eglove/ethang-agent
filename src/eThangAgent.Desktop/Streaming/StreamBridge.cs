using System.Threading.Channels;

namespace eThangAgent.Desktop.Streaming;

/// <summary>
/// Bridges agent-loop stream callbacks (arbitrary threads) to a UI sink. Callbacks
/// only write to an unbounded channel; a single reader pumps events to the sink in order.
/// Event-driven — no polling timer. The channel lives for one turn; MarkTurnComplete ends it.
/// Production sinks marshal onto the UI thread so <see cref="ViewModels.TranscriptViewModel"/>
/// keeps its UI-thread-only contract. Tests must call <see cref="Start"/> after construction —
/// the pump only runs once started.
/// </summary>
public sealed class StreamBridge(Action<UiStreamEvent> sink)
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
        _ = Task.Run(_pump.RunAsync);
    }

    public void MarkTurnComplete() => _channel.Writer.TryComplete();

    public Task DrainUntilIdleAsync(TimeSpan? pollInterval = null) => _drained.Task;
}

internal sealed class StreamBridgePump(
    ChannelReader<UiStreamEvent> reader, Action<UiStreamEvent> sink, TaskCompletionSource drained)
{
    public async Task RunAsync()
    {
        await foreach (var evt in reader.ReadAllAsync()) sink(evt);
        drained.TrySetResult();
    }
}
