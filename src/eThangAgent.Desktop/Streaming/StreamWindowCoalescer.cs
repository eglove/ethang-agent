using System.Diagnostics;
using System.Text;

namespace eThangAgent.Desktop.Streaming;

/// <summary>The sink stream events are ultimately delivered to.</summary>
internal interface IStreamSink
{
  Task DeliverAsync(UiStreamEvent evt);
}

/// <summary>
/// Time-slices text stream events before they reach the sink. Content and reasoning
/// deltas buffer and deliver as one merged event per window (a window is a slice of
/// wall time; delivery happens on flush, driven by the caller's next event or an
/// explicit <see cref="FlushAsync"/> - no timer thread exists, so ordering cannot
/// invert). Structural events (tool call, tool result, notice, iteration end) never
/// buffer: they flush the pending slice first, then deliver verbatim, preserving
/// publication order and the block-close contract the transcript relies on.
/// <para><b>Bounded growth:</b> a fragment whose arrival would push the buffer past
/// <c>MaxChars</c> flushes the buffer first, so accumulation - the per-render cost
/// driver - stays bounded no matter the arrival pattern. A single fragment larger
/// than the ceiling still delivers whole: splitting it would copy it anyway, and
/// one provider fragment is not accumulation.</para>
/// <para><b>Thread contract:</b> one instance per turn, driven only by the bridge
/// pump (single consumer); the clock is injectable for determinism. Not thread-safe
/// by design - callers serialize through the pump.</para>
/// </summary>
internal sealed class StreamWindowCoalescer(IStreamSink sink, double windowSeconds, Func<double>? clock = null) : IStreamSink
{
  private const int MaxChars = 32 * 1024;

  private readonly IStreamSink _sink = sink ?? throw new ArgumentNullException(nameof(sink));
  private readonly Func<double> _clock = clock ?? DefaultClock;
  private readonly Queue<(string Text, bool IsReasoning)> _buffer = [];
  private double? _windowStart;

  public Task ContentDeltaAsync(string text) => BufferTextAsync(text, isReasoning: false);

  public Task ReasoningDeltaAsync(string text) => BufferTextAsync(text, isReasoning: true);

  public Task IterationEndAsync() => StructuralAsync(new UiStreamEvent.IterationEnd());

  public Task ToolCallAsync(string name, string arguments) => StructuralAsync(new UiStreamEvent.ToolCallEvent(name, arguments));

  public Task ToolResultAsync(string name, string summary, string fullContent, bool isError) => StructuralAsync(new UiStreamEvent.ToolResultEvent(name, summary, fullContent, isError));

  public Task NoticeAsync(string text) => StructuralAsync(new UiStreamEvent.Notice(text));

  /// <summary>Delivers verbatim, bypassing the buffer. Part of <see cref="IStreamSink"/>
  ///     so coalescers and bridges can nest.</summary>
  public Task DeliverAsync(UiStreamEvent evt)
  {
    ArgumentNullException.ThrowIfNull(evt);
    return _sink.DeliverAsync(evt);
  }

  /// <summary>Delivers whatever is buffered now. The structural-event path and the
  ///     turn-completion drain both land here.</summary>
  public Task FlushAsync() => DeliverBufferedSliceAsync();

  /// <summary>Buffers one fragment, closing the open slice first when this fragment
  ///     must not merge into it: a kind boundary (reasoning vs content), an expired
  ///     window, or a ceiling-crossing accumulation.</summary>
  private async Task BufferTextAsync(string text, bool isReasoning)
  {
    ArgumentNullException.ThrowIfNull(text);
    if (text.Length == 0)
    {
      return; // empty fragment - no information, no slice change
    }

    if (_buffer.Count > 0 && (_buffer.Peek().IsReasoning != isReasoning
        || _clock() - (_windowStart ?? _clock()) >= windowSeconds
        || TotalBufferedChars() + text.Length > MaxChars))
    {
      await DeliverBufferedSliceAsync().ConfigureAwait(false);
    }

    _windowStart ??= _clock();
    _buffer.Enqueue((text, isReasoning));
  }

  private async Task StructuralAsync(UiStreamEvent evt)
  {
    await DeliverBufferedSliceAsync().ConfigureAwait(false);
    await _sink.DeliverAsync(evt).ConfigureAwait(false);
  }

  /// <summary>Delivers the buffer as one event per kind-run, preserving order. A
  ///     mixed-kind buffer (only possible mid-accumulation) flushes as separate
  ///     kind-runs rather than merging across kinds.</summary>
  private async Task DeliverBufferedSliceAsync()
  {
    while (_buffer.Count > 0)
    {
      bool isReasoning = _buffer.Peek().IsReasoning;
      StringBuilder text = new();
      while (_buffer.Count > 0 && _buffer.Peek().IsReasoning == isReasoning)
      {
        _ = text.Append(_buffer.Dequeue().Text);
      }

      UiStreamEvent evt = isReasoning
          ? new UiStreamEvent.Reasoning(text.ToString())
          : new UiStreamEvent.Delta(text.ToString());
      await _sink.DeliverAsync(evt).ConfigureAwait(false);
    }

    _windowStart = null;
  }

  private int TotalBufferedChars()
  {
    int total = 0;
    foreach ((string text, _) in _buffer)
    {
      total += text.Length;
    }

    return total;
  }

  private static double DefaultClock() => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;
}
