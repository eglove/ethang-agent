using System.ComponentModel;
using System.Globalization;
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
///     and an animated spinner whose frame advances via <see cref="Tick"/> while a turn
///     is in flight. Tool elapsed timing lives in the transcript's tool cards
///     (<see cref="TranscriptViewModel"/>), not here. Phase may be set from any thread
///     (agent callbacks run off-UI); property-changed notifications marshal onto the UI
///     thread so bindings stay safe.
/// </summary>
internal sealed class StatusViewModel(string provider, string modelId, string effort) : INotifyPropertyChanged
{
  // Identical glyph set to the terminal spinner (Program.SpinnerFrames).
  private static readonly string[] Frames =
  [
        "\u280b", "\u2819", "\u2839", "\u2838", "\u283c",
          "\u2834", "\u2826", "\u2827", "\u2807", "\u280f",
  ];

  /// <summary>The session's running spend (issue #24): seeded from the persisted rollup
  ///     at tab open, advanced by each recorded call. Not thread-safe; every mutation runs
  ///     on the status bar's own marshaled thread.</summary>
  private readonly UsageTally _usage = new();

  private int _frame;

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
      if (field == value)
      {
        return;
      }

      field = value;
      RaiseAll();
    }
  } = TurnPhase.Ready;

  /// <summary>The workspace's current git branch ("feature/demo"), or "" when
  ///     the workspace is not a git repository. Fed by the session's
  ///     <see cref="GitBranchWatcher"/> (event-driven on .git/HEAD changes).</summary>
  public string Branch
  {
    get;
    set
    {
      if (field == value)
      {
        return;
      }

      field = value;
      Raise(nameof(Branch));
    }
  } = "";

  /// <summary>Current spinner frame; empty whenever the turn phase is Ready.</summary>
  public string Spinner => Phase == TurnPhase.Ready ? "" : Frames[_frame];

  /// <summary>CTX statusline text, e.g. "CTX 148.2K/1M, 15%". "" until the first
  ///     context update; unknown window shows session totals only. A provider-reported
  ///     prompt-cache hit adds ", cache 82%" and a reported reasoning count adds
  ///     ", reasoning 1.2K" (issue #262); a provider that reports neither renders
  ///     exactly the pre-#262 line.</summary>
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

  /// <summary>The session's cumulative spend (issue #24), e.g. "$0.0123". "" until the
  ///     session has recorded a call; a session whose rows could not be priced shows its
  ///     token total instead ("12.3K tok"), because a cost of zero would be a lie about
  ///     spend nobody measured (B13).</summary>
  public string CostDisplay
  {
    get;
    private set
    {
      if (field == value)
      {
        return;
      }

      field = value;
      Raise(nameof(CostDisplay));
    }
  } = "";

  /// <summary>Hover text for the cost segment: what the number covers and what it does
  ///     not, so a partial cost is never read as the whole bill.</summary>
  public string CostBreakdownText
  {
    get;
    private set
    {
      if (field == value)
      {
        return;
      }

      field = value;
      Raise(nameof(CostBreakdownText));
    }
  } = "";

  /// <summary>Seeds the accumulator from the persisted rollup at tab open, so a resumed or
  ///     reopened session starts from its real history instead of from zero. Must run
  ///     before the first turn's events arrive.</summary>
  public void SeedUsage(UsageTotal totals)
  {
    ArgumentNullException.ThrowIfNull(totals);
    _usage.Seed(totals);
    RenderUsage();
  }

  /// <summary>Adds one recorded provider call (a `usage` event, or an in-process
  ///     callback). Event-driven by design: the ledger row is written in the host process,
  ///     so polling for it would be the only alternative.</summary>
  public void AddUsage(UsageRow row)
  {
    ArgumentNullException.ThrowIfNull(row);
    _usage.Add(row);
    RenderUsage();
  }

  /// <summary>Re-renders both cost strings from the accumulator.</summary>
  private void RenderUsage()
  {
    UsageTotal totals = _usage.Totals;
    if (totals.Calls == 0)
    {
      CostDisplay = "";
      CostBreakdownText = "";
      return;
    }

    // A priced session shows money; an unpriced one shows the tokens it did measure. The
    // two are never mixed into one number, because a partial cost presented as the total
    // is exactly the overstatement/understatement the ledger's null rules exist to avoid.
    CostDisplay = totals.Cost is { } cost
        ? FormatCost(cost)
        : $"{FormatTokens((int)Math.Min(totals.TotalTokens, int.MaxValue))} tok";
    string unpriced = totals.UnpricedCalls > 0
        ? $" · {totals.UnpricedCalls} unpriced call(s) not included"
        : "";
    CostBreakdownText = $"{FormatTokens((int)Math.Min(totals.TotalTokens, int.MaxValue))} tokens "
        + $"({FormatTokens((int)Math.Min(totals.InputTokens, int.MaxValue))} in · "
        + $"{FormatTokens((int)Math.Min(totals.OutputTokens, int.MaxValue))} out) · "
        + $"{totals.Calls} call(s){unpriced}";
  }

  /// <summary>Formats a cost: cents and below keep four decimals so a single cheap call is
  ///     visible at all, dollars and above keep two. Invariant culture — the status line is
  ///     a readout, not a locale-formatted amount.</summary>
  private static string FormatCost(decimal cost)
      => cost < 1m
          ? $"${cost.ToString("0.0000", CultureInfo.InvariantCulture)}"
          : $"${cost.ToString("0.00", CultureInfo.InvariantCulture)}";
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

    // Cache and reasoning segments appear ONLY when the provider reported them (issue
    // #262): an unreported count renders exactly as it did before the fields existed,
    // and a provider-reported zero renders as a real zero.
    string cache = status.CacheHitPercent is { } hit ? $", cache {hit:0}%" : "";
    string reasoning = status.LastReasoningTokens is { } tokens
        ? $", reasoning {FormatTokens(tokens)}"
        : "";
    ContextDisplay = status.ContextWindow is { } window
        ? $"CTX {totals}/{FormatTokens(window)}, {status.UtilizationPercent:0}%{cache}{reasoning}"
        : $"CTX {totals} total{cache}{reasoning}";

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
      _ => tokens.ToString(CultureInfo.InvariantCulture),
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

  /// <summary>Advances the spinner frame. Called by an 80 ms DispatcherTimer in the
  ///     view while busy; a no-op when Ready so an idle status bar never re-renders.</summary>
  public void Tick()
  {
    if (Phase == TurnPhase.Ready)
    {
      return;
    }

    _frame = (_frame + 1) % Frames.Length;
    Raise(nameof(Spinner));
  }

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
