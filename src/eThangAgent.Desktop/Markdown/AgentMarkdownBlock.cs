using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using MarkView.Avalonia;

namespace eThangAgent.Desktop.Markdown;

/// <summary>Which transcript voice a block renders. Assistant renders markdown;
///     Reasoning renders plain while open and dimmed markdown once closed.</summary>
internal enum AgentMarkdownVariant
{
  Assistant,
  Reasoning,
}

/// <summary>Transcript block backed by MarkView's <see cref="MarkdownViewer"/>:
///     renders LIVE markdown while open - re-renders coalesced to at most one
///     visual rebuild per <see cref="ThrottleInterval"/> (deltas are cheap; full
///     document rebuilds are not) - and an immediate final render on close. Owns
///     MarkdownText/IsOpen/Variant styled properties and renders itself from
///     property changes alone: the UI only binds, nothing outside calls render
///     (the shipped bug was a control that waited for a caller that never came).
///     The one exception is <see cref="FlushPendingRender"/>, a test seam that
///     collapses the throttle clock; the coalescing timer calls the same drain.
///     Link clicks route through the app-wide LinkClickedEvent class handler to
///     <see cref="MarkdownLinkLauncher.TryOpen"/>, the http(s)-only gate every
///     entry point inherits.</summary>
internal class AgentMarkdownBlock : ContentControl
{
  public static readonly StyledProperty<string> MarkdownTextProperty =
      AvaloniaProperty.Register<AgentMarkdownBlock, string>(nameof(MarkdownText), string.Empty);

  public static readonly StyledProperty<bool> IsOpenProperty =
      AvaloniaProperty.Register<AgentMarkdownBlock, bool>(nameof(IsOpen), true);

  public static readonly StyledProperty<AgentMarkdownVariant> VariantProperty =
      AvaloniaProperty.Register<AgentMarkdownBlock, AgentMarkdownVariant>(nameof(Variant), AgentMarkdownVariant.Assistant);

  public static readonly StyledProperty<TimeSpan> ThrottleIntervalProperty =
      AvaloniaProperty.Register<AgentMarkdownBlock, TimeSpan>(nameof(ThrottleInterval), TimeSpan.FromMilliseconds(150));

  private const double ReasoningOpacity = 0.7;

  private MarkdownViewer? _viewer;
  private TextBlock? _plainBlock;
  private DispatcherTimer? _throttle;
  private bool _pending;

  /// <summary>Whether the coalescing window is currently armed. A detached block never is: the
  ///     dispatcher roots an armed timer and the timer's tick delegate targets this block, so an
  ///     armed window keeps the block, its viewer and the rendered tree beneath it reachable.</summary>
  internal bool IsThrottleArmed => _throttle?.IsEnabled ?? false;

  /// <summary>How many scrollbar thumbs inside this block are mid-drag. A re-render while one is
  ///     held is DEFERRED, because the re-render replaces the scroll viewer, its scrollbar, and the
  ///     thumb - and the pointer keeps driving the OLD thumb it captured. That orphaned the gesture
  ///     mid-way: the thumb advanced a couple of characters and then the visible block stopped
  ///     moving, and only releasing and grabbing again could advance it further (measured: the
  ///     orphan's offset climbed 626 -> 783 -> 939 while the block on screen sat at 469.9).
  ///     <para>A count rather than a flag, because a document holds many code blocks and only the
  ///     one under the pointer is being dragged, but nothing prevents two starting before either
  ///     finishes (a second pointer, or a synthetic event).</para></summary>
  private int _activeDrags;

  /// <summary>A render that arrived while a thumb was held, to run once the drag ends. Without
  ///     this the deferral would silently DROP the delta and the block would stop streaming.</summary>
  private bool _renderDeferredByDrag;

  /// <summary>The thumbs currently watched for capture loss, so the subscription is added once and
  ///     removed when it fires.</summary>
  private readonly HashSet<Thumb> _captureWatched = [];

  /// <summary>How many times this block has rebuilt its visual, exposed so a test can
  ///     assert the coalescing CONTRACT (a bounded number of rebuilds for a burst of
  ///     deltas) instead of inferring it from wall-clock-dependent text. Counting here is
  ///     the honest signal: the throttle's whole promise is about rebuild COUNT, and a
  ///     value read back from <see cref="MarkdownText"/> reports whichever delta happened
  ///     to be pending when the clock last fired.</summary>
  internal int RenderCount { get; private set; }

  public string MarkdownText
  {
    get => GetValue(MarkdownTextProperty);
    set => SetValue(MarkdownTextProperty, value);
  }

  public bool IsOpen
  {
    get => GetValue(IsOpenProperty);
    set => SetValue(IsOpenProperty, value);
  }

  public AgentMarkdownVariant Variant
  {
    get => GetValue(VariantProperty);
    set => SetValue(VariantProperty, value);
  }

  public TimeSpan ThrottleInterval
  {
    get => GetValue(ThrottleIntervalProperty);
    set => SetValue(ThrottleIntervalProperty, value);
  }

  /// <summary>Subscribes the drag watch once, as CLASS handlers on this block. They are attached
  ///     here rather than per document because the thumbs a drag starts on do not exist until the
  ///     scrollbar template is applied, and a class handler sees every thumb this block will ever
  ///     contain without enumerating them.</summary>
  public AgentMarkdownBlock()
  {
    AddHandler(Thumb.DragStartedEvent, OnThumbDragStarted, RoutingStrategies.Bubble);
    AddHandler(Thumb.DragCompletedEvent, OnThumbDragCompleted, RoutingStrategies.Bubble);
  }

  // Self-rendering: property changes drive every render. Text changes coalesce
  // while open; close/variant changes are terminal and render immediately.
  protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
  {
    base.OnPropertyChanged(change);
    if (change.Property == MarkdownTextProperty)
    {
      ScheduleRender();
    }
    else if (change.Property == IsOpenProperty || change.Property == VariantProperty)
    {
      RenderNow();
    }
  }

  /// <summary>Ends the coalescing window when this block leaves the visual tree: a timer left
  ///     armed here keeps this block and its rendered tree reachable for the life of the
  ///     process, and a transcript detaches blocks on every scroll and every session close.
  ///     <para>A pending render is DRAINED rather than dropped, so the rendered content matches
  ///     MarkdownText for whatever re-attaches this block. The drag deferral is reset because a
  ///     detached control holds no pointer capture.</para></summary>
  protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
  {
    base.OnDetachedFromVisualTree(e);
    _activeDrags = 0;
    _renderDeferredByDrag = false;
    RenderNow();
  }

  /// <summary>Drains a coalesced pending render immediately; the throttle timer's tick calls
  ///     this, and tests call it to collapse the clock.
  ///     <para>THE WINDOW STAYS OPEN ACROSS A FLUSH. Closing it here left the next delta to
  ///     re-open it by rendering IMMEDIATELY, so a delta arriving just after a tick - the
  ///     ordinary shape of a stream, and the certain shape of a slow machine whose tick lands
  ///     between two deltas - rebuilt the whole document on that delta instead of coalescing it
  ///     into the next window. The block then reported the newest text where the contract says
  ///     the window's text is still showing, which is how a test that defers a render and
  ///     asserts the old text saw the new one (issue #380). Only a close, a variant change, or
  ///     a drag deferral ends the window: those are terminal, a flush is not.</para></summary>
  public void FlushPendingRender()
  {
    // A tick that finds nothing pending keeps its window running: the timer is the window, and
    // stopping it here is what made the NEXT delta re-open the window with a render.
    if (!_pending)
    {
      return;
    }

    _pending = false;
    RenderDeferred();
  }

  /// <summary>Renders, unless a thumb inside this block is being dragged - in which case the render
  ///     waits for the drag to end. Every render entry point goes through here so no path can
  ///     rebuild the tree under the pointer.</summary>
  private void RenderDeferred()
  {
    if (_activeDrags > 0)
    {
      _renderDeferredByDrag = true;
      return;
    }

    RenderMarkdown();
  }

  private void ScheduleRender()
  {
    if (!IsOpen)
    {
      RenderNow();
      return;
    }

    if (Variant == AgentMarkdownVariant.Reasoning)
    {
      RenderPlain(); // a plain Text update is cheap: every delta, no throttle
      return;
    }

    if (_throttle is null)
    {
      // First content (or first after a flush): render now, then open the
      // coalescing window. Subsequent deltas inside the window only set _pending.
      RenderDeferred();
      _throttle = new DispatcherTimer { Interval = ThrottleInterval };
      _throttle.Tick += (_, _) => FlushPendingRender();
      _throttle.Start();
    }
    else
    {
      _pending = true;
    }
  }

  private void RenderNow()
  {
    _throttle?.Stop();
    _throttle = null;
    _pending = false;
    if (Variant == AgentMarkdownVariant.Reasoning && IsOpen)
    {
      RenderPlain();
      return;
    }

    RenderDeferred();
  }

  private MarkdownViewer Viewer()
  {
    // No link wiring here on purpose: App.axaml.cs registers ONE global
    // LinkClickedEvent class handler -> MarkdownLinkLauncher, so every viewer
    // (this block's and any future surface's) shares the single http(s) gate.
    _viewer ??= new MarkdownViewer();
    return _viewer;
  }

  private void RenderMarkdown()
  {
    RenderCount++;
    MarkdownViewer viewer = Viewer();
    viewer.Opacity = Variant == AgentMarkdownVariant.Reasoning ? ReasoningOpacity : 1.0;
    // The offsets are captured BEFORE the markdown is replaced. This block reuses ONE viewer
    // instance, so assigning Markdown rebuilds the very tree the offsets live in - reading them
    // afterwards returns zeros, silently, and every streamed delta would snap the code block back
    // to its left edge mid-drag.
    IReadOnlyList<double> offsets = CodeBlockScroller.CaptureOffsets(viewer);
    viewer.Markdown = MarkdownText ?? string.Empty;
    Content = viewer;
    CodeBlockScroller.Wrap(viewer, offsets);
  }

  /// <summary>Counts a drag starting, and watches THAT thumb for the ending DragCompleted cannot
  ///     report.
  ///     <para>These are CLASS handlers on the block, not subscriptions to individual thumbs, and
  ///     that is load-bearing: a freshly rendered document has no thumbs yet - the scrollbar
  ///     template is applied during the next layout pass - so an enumeration at render time finds
  ///     none and the watch silently never attaches. Thumb's drag events BUBBLE, so one handler on
  ///     the block sees every thumb it will ever contain, whenever that thumb appears.</para>
  ///     <para>A gesture can end without DragCompleted: the pointer's capture is lost when the
  ///     window deactivates or the thumb is detached. Measured - capture loss raises no
  ///     DragCompleted - so a count that only ever decremented there would stay above zero and
  ///     defer every later render forever, silently freezing the block's streaming.</para>
  ///     <para>The capture-loss subscription therefore lives on the THUMB, not on this block:
  ///     PointerCaptureLost is a DIRECT routed event, so a handler on an ancestor never sees it,
  ///     while the thumb that held the capture always does. It is attached here rather than at
  ///     render time because a freshly rendered document has no thumbs until the scrollbar
  ///     template is applied, and DragStarted is the first moment one certainly exists.</para></summary>
  private void OnThumbDragStarted(object? sender, VectorEventArgs e)
  {
    _activeDrags++;
    // e.Source, never sender: this is a BUBBLING class handler, so sender is always this block
    // and only the event's own Source names the thumb that started the drag.
    if (e.Source is Thumb thumb && _captureWatched.Add(thumb))
    {
      thumb.PointerCaptureLost += OnThumbCaptureLost;
    }
  }

  /// <summary>A lost capture ends the gesture. The count is CLEARED rather than decremented:
  ///     capture is per pointer, so losing it means nothing is being held any more, and a stale
  ///     count is the one failure mode that would stop the block rendering for good.</summary>
  private void OnThumbCaptureLost(object? sender, PointerCaptureLostEventArgs e)
  {
    if (sender is Thumb thumb)
    {
      thumb.PointerCaptureLost -= OnThumbCaptureLost;
      _ = _captureWatched.Remove(thumb);
    }

    _activeDrags = 0;
    ResumeDeferredRender();
  }

  private void OnThumbDragCompleted(object? sender, VectorEventArgs e)
  {
    if (_activeDrags > 0)
    {
      _activeDrags--;
    }

    ResumeDeferredRender();
  }

  /// <summary>Applies a render that was held off by a drag, once no drag is held. The position the
  ///     user dragged to survives because RenderMarkdown captures it before rebuilding.</summary>
  private void ResumeDeferredRender()
  {
    if (_activeDrags > 0 || !_renderDeferredByDrag)
    {
      return;
    }

    _renderDeferredByDrag = false;
    _pending = false;
    _throttle?.Stop();
    _throttle = null;
    RenderMarkdown();
  }

  private void RenderPlain()
  {
    RenderCount++;
    _plainBlock ??= new TextBlock { FontStyle = FontStyle.Italic, TextWrapping = TextWrapping.Wrap };
    _plainBlock.Text = MarkdownText ?? string.Empty;
    _plainBlock.Opacity = ReasoningOpacity;
    Content = _plainBlock;
  }
}
