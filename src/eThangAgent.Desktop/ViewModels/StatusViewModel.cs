using System.ComponentModel;
using System.Diagnostics;
using Avalonia.Threading;
using eThangAgent.ModelDomain;

namespace eThangAgent.Desktop.ViewModels;

/// <summary>Turn phase for the status bar — parity with the terminal StatusLine states.</summary>
internal enum TurnPhase
{
  Ready,
  Thinking,
  Streaming,
}

/// <summary>
///     Status bar state: the session's provider, the model id, the reasoning effort,
///     an animated spinner whose frame advances via <see cref="Tick"/> while a turn
///     is in flight, and the running tool's elapsed-time line. Phase may be set from
///     any thread (agent callbacks run off-UI); property-changed notifications marshal
///     onto the UI thread so bindings stay safe.
/// </summary>
internal sealed class StatusViewModel(string provider, string modelId, string effort, Func<double>? secondsClock = null) : INotifyPropertyChanged
{
  // Identical glyph set to the terminal spinner (Program.SpinnerFrames).
  private static readonly string[] Frames =
  [
        "\u280b", "\u2819", "\u2839", "\u2838", "\u283c",
          "\u2834", "\u2826", "\u2827", "\u2807", "\u280f",
    ];

  private int _frame;

  // Seconds source for tool elapsed timing; injectable so tests drive it
  // deterministically instead of sleeping.
  private readonly Func<double>? _injectedClock = secondsClock;
  private string? _toolName;
  private double _toolStartSeconds;
  private string? _frozenToolDisplay;

  /// <summary>The AI provider this session is wired for; fixed for the session's lifetime.</summary>
  public string Provider { get; } = provider;

  public string ModelId
  {
    get;
    set
    {
      if (field == value)
      {
        return;
      }

      field = value;
      Raise(nameof(ModelId));
    }
  } = modelId;

  /// <summary>The session's current reasoning effort as a display string — "Model default"
  ///     when no level is set. Updated by the effort picker and the shell's restore-on-open
  ///     (callers own the display-name vocabulary, <see cref="EffortLevels"/>).</summary>
  public string Effort
  {
    get;
    set
    {
      if (field == value)
      {
        return;
      }

      field = value;
      Raise(nameof(Effort));
    }
  } = effort;

  public TurnPhase Phase
  {
    get;
    set
    {
      // Back to idle: no stale tool line survives the turn - cleared on every Ready
      // assignment, self-transitions included (ToolDisplay's own guard keeps it
      // from re-notifying when already empty).
      if (value == TurnPhase.Ready)
      {
        _toolName = null;
        _frozenToolDisplay = null;
        ToolDisplay = "";
      }

      if (field == value)
      {
        return;
      }

      field = value;
      RaiseAll();
    }
  } = TurnPhase.Ready;

  /// <summary>Current spinner frame; empty whenever the turn phase is Ready.</summary>
  public string Spinner => Phase == TurnPhase.Ready ? "" : Frames[_frame];

  /// <summary>Tool elapsed line, e.g. "read 0.8s", or "bash 12.3s \u2717" on an errored
  ///     result. "" when idle; cleared when the phase returns to Ready.</summary>
  public string ToolDisplay
  {
    get;
    private set
    {
      if (field == value)
      {
        return;
      }

      field = value;
      Raise(nameof(ToolDisplay));
    }
  } = "";

  /// <summary>CTX statusline text, e.g. "CTX 148.2K/1M, 15%". "" until the first
  ///     context update; unknown window shows session totals only.</summary>
  public string ContextDisplay
  {
    get;
    private set
    {
      if (field == value)
      {
        return;
      }

      field = value;
      Raise(nameof(ContextDisplay));
    }
  } = "";

  /// <summary>Hover breakdown line, or "" when no breakdown estimate exists.</summary>
  public string ContextBreakdownText
  {
    get;
    private set
    {
      if (field == value)
      {
        return;
      }

      field = value;
      Raise(nameof(ContextBreakdownText));
    }
  } = "";

  /// <summary>Applies a new context snapshot (null clears the display). Any thread.</summary>
  public void SetContext(ContextSnapshot? snapshot)
  {
    if (snapshot is null)
    {
      ContextDisplay = "";
      ContextBreakdownText = "";
      return;
    }

    ContextStatus status = snapshot.Status;
    string totals = FormatTokens(status.LastInputTokens ?? (int)status.TotalInputTokens);
    ContextDisplay = status.ContextWindow is { } window
        ? $"CTX {totals}/{FormatTokens(window)}, {status.UtilizationPercent:0}%"
        : $"CTX {totals} total";

    ContextBreakdownText = snapshot.Breakdown is not { } breakdown
        ? ""
        : $"System ~{FormatTokens(breakdown.SystemPromptTokens ?? 0)} · Messages ~{FormatTokens(breakdown.MessageTokens ?? 0)} · Tools ~{FormatTokens(breakdown.ToolTokens ?? 0)}";
  }

  /// <summary>Formats token counts: raw below 1000, one-decimal K below a million,
  ///     and M above that (exactly one million renders as "1M").</summary>
  private static string FormatTokens(int tokens)
  {
    return tokens switch
    {
      >= 1_000_000 => tokens == 1_000_000 ? "1M" : $"{tokens / 1_000_000.0:0.#}M",
      >= 1000 => $"{tokens / 1000.0:0.#}K",
      _ => tokens.ToString(System.Globalization.CultureInfo.InvariantCulture),
    };
  }

  /// <summary>Human-readable phase label: Ready / Thinking… / Streaming…</summary>
  public string PhaseLabel => Phase switch
  {
    TurnPhase.Thinking => "Thinking\u2026",
    TurnPhase.Streaming => "Streaming\u2026",
    TurnPhase.Ready => "Ready",
    _ => "Ready",
  };

  /// <summary>Advances the spinner frame and refreshes the live tool elapsed. Called by
  ///     an 80 ms DispatcherTimer in the view while busy; a no-op when Ready so an idle
  ///     status bar never re-renders.</summary>
  public void Tick()
  {
    if (Phase == TurnPhase.Ready)
    {
      return;
    }

    _frame = (_frame + 1) % Frames.Length;
    Raise(nameof(Spinner));
    UpdateToolDisplay();
  }

  /// <summary>Starts (or restarts) timing a tool call: the display shows the tool name
  ///     at zero elapsed immediately and advances on each <see cref="Tick"/> while the
  ///     tool runs.</summary>
  public void BeginTool(string name)
  {
    _toolName = name;
    _toolStartSeconds = SecondsNow();
    _frozenToolDisplay = null;
    ToolDisplay = Compose(name, 0.0, isError: false);
  }

  /// <summary>Freezes the final elapsed for the running tool, appending the error marker
  ///     on a failed result. The frozen value survives later Ticks until the next
  ///     <see cref="BeginTool"/> or a return to Ready. No-op when no tool is running.</summary>
  public void EndTool(bool isError)
  {
    if (_toolName is null)
    {
      return;
    }

    _frozenToolDisplay = Compose(_toolName, SecondsNow() - _toolStartSeconds, isError);
    ToolDisplay = _frozenToolDisplay;
  }

  /// <summary>Refreshes the live (unfrozen) tool elapsed; driven by <see cref="Tick"/>.</summary>
  private void UpdateToolDisplay()
  {
    if (_toolName is null || _frozenToolDisplay is not null)
    {
      return;
    }

    ToolDisplay = Compose(_toolName, SecondsNow() - _toolStartSeconds, isError: false);
  }

  private double SecondsNow() => _injectedClock is { } clock ? clock() : Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;

  private static string Compose(string name, double seconds, bool isError)
    => $"{name} {FormatElapsed(seconds)}{(isError ? " \u2717" : "")}";

  /// <summary>Formats tool elapsed seconds: one decimal below a minute, m:ss above.</summary>
  private static string FormatElapsed(double seconds)
    => seconds < 60 ? $"{seconds:0.0}s" : $"{(int)(seconds / 60)}:{(int)seconds % 60:00}";

  public event PropertyChangedEventHandler? PropertyChanged;

  private void RaiseAll()
  {
    Raise(nameof(Phase));
    Raise(nameof(Spinner));
    Raise(nameof(PhaseLabel));
  }

  private void Raise(string name)
  {
    PropertyChangedEventHandler? handlers = PropertyChanged;
    if (handlers is null)
    {
      return;
    }

    if (Dispatcher.UIThread.CheckAccess())
    {
      handlers(this, new PropertyChangedEventArgs(name));
    }
    else
    {
      Dispatcher.UIThread.Post(() => handlers(this, new PropertyChangedEventArgs(name)));
    }
  }
}
