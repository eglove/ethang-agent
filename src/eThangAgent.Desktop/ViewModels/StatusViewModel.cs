using System.ComponentModel;
using Avalonia.Threading;

namespace eThangAgent.Desktop.ViewModels;

/// <summary>Turn phase for the status bar — parity with the terminal StatusLine states.</summary>
internal enum TurnPhase
{
  Ready,
  Thinking,
  Streaming,
}

/// <summary>
///     Status bar state: model id and an animated spinner whose frame advances via
///     <see cref="Tick"/> while a turn is in flight. Phase may be set from any thread
///     (agent callbacks run off-UI); property-changed notifications marshal onto the UI
///     thread so bindings stay safe.
/// </summary>
internal sealed class StatusViewModel(string modelId) : INotifyPropertyChanged
{
  // Identical glyph set to the terminal spinner (Program.SpinnerFrames).
  private static readonly string[] Frames =
  [
      "\u280b", "\u2819", "\u2839", "\u2838", "\u283c",
        "\u2834", "\u2826", "\u2827", "\u2807", "\u280f",
    ];

  private int _frame;

  public string ModelId { get; } = modelId;

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

  /// <summary>Current spinner frame; empty whenever the turn phase is Ready.</summary>
  public string Spinner => Phase == TurnPhase.Ready ? "" : Frames[_frame];

  /// <summary>Human-readable phase label: Ready / Thinking… / Streaming…</summary>
  public string PhaseLabel => Phase switch
  {
    TurnPhase.Thinking => "Thinking\u2026",
    TurnPhase.Streaming => "Streaming\u2026",
    TurnPhase.Ready => "Ready",
    _ => "Ready",
  };

  /// <summary>Advances the spinner frame. Called by an 80 ms DispatcherTimer in the view
  ///     while busy; a no-op when Ready so an idle status bar never re-renders.</summary>
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
