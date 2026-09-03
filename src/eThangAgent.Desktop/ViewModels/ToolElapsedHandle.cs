using System.ComponentModel;

namespace eThangAgent.Desktop.ViewModels;

/// <summary>Mutable elapsed-time handle for one running tool's call card. The
///     transcript entry record keeps an immutable reference to it; the elapsed
///     tick mutates the handle (raising <see cref="PropertyChanged"/> for
///     <c>ElapsedDisplay</c>... corrected: <c>Display</c>) instead of replacing
///     the entry, so the card's Expander container is never rebuilt while the
///     count-up runs — the fix for the chevron re-animation / cannot-expand
///     dropdown bug. Display string vocabulary stays in <see cref="ToolElapsed"/>.</summary>
internal sealed class ToolElapsedHandle(string display) : INotifyPropertyChanged
{
  /// <summary>The formatted elapsed line (e.g. "0.8s"). Empty when unknown, so
  ///     restored transcripts render unchanged.</summary>
  public string Display
  {
    get;
    set
    {
      if (field == value)
      {
        return;
      }

      field = value;
      Raise();
    }
  } = display;

  public event PropertyChangedEventHandler? PropertyChanged;

  private void Raise()
  {
    PropertyChangedEventHandler? handlers = PropertyChanged;
    if (handlers is null)
    {
      return;
    }

    handlers(this, new PropertyChangedEventArgs(nameof(Display)));
  }
}
