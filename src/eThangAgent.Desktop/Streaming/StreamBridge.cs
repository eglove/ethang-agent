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
/// <para><b>Coalescing:</b> with <c>coalesce</c> set, text deltas and reasoning deltas
/// are time-sliced by a <see cref="StreamWindowCoalescer"/> inside the pump before they
/// reach the sink - one sink delivery per window instead of one per token. Structural
/// events pass through in order, flushing pending text first, and the final flush runs
/// before the drain task settles, so the await-everything contract is unchanged.
/// Without the flag the legacy per-event delivery is byte-identical.</para>
/// <para><b>Fault semantics:</b> if the sink throws, the exception is forwarded to every
/// <see cref="DrainUntilIdleAsync"/> awaiter via <c>TrySetException</c> — the task never
/// hangs. Remaining buffered events are abandoned on the first sink fault. The pump task
/// itself stores its <see cref="Task"/> and adds a no-op continuation so the fault is
/// always observed and never raises <see cref="TaskScheduler.UnobservedTaskException"/>.</para>
/// </summary>
internal sealed class StreamBridge(Func<UiStreamEvent, Task> sink, bool coalesce = false)
{
  private readonly Channel<UiStreamEvent> _channel =
      Channel.CreateUnbounded<UiStreamEvent>(new UnboundedChannelOptions { SingleReader = true });
  private readonly TaskCompletionSource _drained = new(TaskCreationOptions.RunContinuationsAsynchronously);

  public Action<string> OnContentDelta => text => _channel.Writer.TryWrite(new UiStreamEvent.Delta(text));
  public Action<string> OnReasoningDelta => text => _channel.Writer.TryWrite(new UiStreamEvent.Reasoning(text));
  public Action OnIterationEnd => () => _channel.Writer.TryWrite(new UiStreamEvent.IterationEnd());
  public Action<string, string> OnToolCall => (name, args) => _channel.Writer.TryWrite(new UiStreamEvent.ToolCallEvent(name, args));
  public Action<string, string, string, bool> OnToolResult => (name, summary, fullContent, isError) => _channel.Writer.TryWrite(new UiStreamEvent.ToolResultEvent(name, summary, fullContent, isError));
  public Action<string> OnNotice => text => _channel.Writer.TryWrite(new UiStreamEvent.Notice(text));

  // Time-slice window for text deltas; flush cadence parity with the spinner timer.
  internal const double CoalesceWindowSeconds = 0.08;

  public void Start()
  {
    StreamBridgePump pump = new(_channel.Reader, sink, _drained, coalesce);
    // Store the task and attach a no-op continuation so any unhandled fault
    // is observed; the real fault path goes through _drained.TrySetException.
    _ = Task.Run(pump.RunAsync).ContinueWith(
        static t => _ = t.Exception, // observe fault
        CancellationToken.None,
        TaskContinuationOptions.OnlyOnFaulted,
        TaskScheduler.Default);
  }

  public void MarkTurnComplete() => _channel.Writer.TryComplete();

  public Task DrainUntilIdleAsync() => _drained.Task;
}

internal sealed class StreamBridgePump(
    ChannelReader<UiStreamEvent> reader,
    Func<UiStreamEvent, Task> sink,
    TaskCompletionSource drained,
    bool coalesce)
{
  // Null in the legacy path: per-event delivery, byte-identical to before.
  private StreamWindowCoalescer? Coalescer { get; } = coalesce
      ? new StreamWindowCoalescer(new FuncSink(sink), StreamBridge.CoalesceWindowSeconds)
      : null;

  public async Task RunAsync()
  {
    try
    {
      await foreach (UiStreamEvent evt in reader.ReadAllAsync())
      {
        if (Coalescer is { } coalescer)
        {
          await Route(coalescer, evt);
        }
        else
        {
          await sink(evt);
        }
      }

      // The channel closed: deliver the tail text slice so DrainUntilIdleAsync keeps
      // meaning "every event was applied" - structural events flushed on arrival.
      if (Coalescer is { } tail)
      {
        await tail.FlushAsync();
      }
    }
    // Named decision (CA1031): the pump is the stream fault boundary — ANY sink
    // failure must be forwarded to the drain awaiter, never crash the process.
#pragma warning disable CA1031 // Do not catch general exception types
    catch (Exception ex)
    {
      _ = drained.TrySetException(ex);
    }
#pragma warning restore CA1031
    finally
    {
      // Guard: if we exited via the catch path, TrySetException already settled the
      // TCS; this call is a no-op.  If we exited normally, this is the only setter.
      _ = drained.TrySetResult();
    }
  }

  private static Task Route(StreamWindowCoalescer coalescer, UiStreamEvent evt) => evt switch
  {
    UiStreamEvent.Delta d => coalescer.ContentDeltaAsync(d.Text),
    UiStreamEvent.Reasoning r => coalescer.ReasoningDeltaAsync(r.Text),
    UiStreamEvent.IterationEnd => coalescer.IterationEndAsync(),
    UiStreamEvent.ToolCallEvent tc => coalescer.ToolCallAsync(tc.Name, tc.Arguments),
    UiStreamEvent.ToolResultEvent tr => coalescer.ToolResultAsync(tr.Name, tr.Summary, tr.FullContent, tr.IsError),
    UiStreamEvent.Notice n => coalescer.NoticeAsync(n.Text),
    _ => Task.CompletedTask,
  };
}

/// <summary>Adapts the bridge's sink delegate to the coalescer's interface.</summary>
internal sealed class FuncSink(Func<UiStreamEvent, Task> sink) : IStreamSink
{
  public Task DeliverAsync(UiStreamEvent evt) => sink(evt);
}
