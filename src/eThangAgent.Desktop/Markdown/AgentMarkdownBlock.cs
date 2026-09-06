using Avalonia;
using Avalonia.Controls;
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

  /// <summary>Drains a coalesced pending render immediately; the throttle timer's
  ///     tick calls this, and tests call it to collapse the clock.</summary>
  public void FlushPendingRender()
  {
    _throttle?.Stop();
    _throttle = null;
    if (!_pending)
    {
      return;
    }

    _pending = false;
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
      RenderMarkdown();
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

    RenderMarkdown();
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
    MarkdownViewer viewer = Viewer();
    viewer.Opacity = Variant == AgentMarkdownVariant.Reasoning ? ReasoningOpacity : 1.0;
    viewer.Markdown = MarkdownText ?? string.Empty;
    Content = viewer;
  }

  private void RenderPlain()
  {
    _plainBlock ??= new TextBlock { FontStyle = FontStyle.Italic, TextWrapping = TextWrapping.Wrap };
    _plainBlock.Text = MarkdownText ?? string.Empty;
    _plainBlock.Opacity = ReasoningOpacity;
    Content = _plainBlock;
  }
}
