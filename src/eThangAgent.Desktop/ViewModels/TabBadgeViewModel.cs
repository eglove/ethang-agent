using CommunityToolkit.Mvvm.ComponentModel;
using eThangAgent.AgentDomain;

namespace eThangAgent.Desktop.ViewModels;

/// <summary>W4.4: the session tab's unread-steering badge. Attach subscribes one
///     <see cref="UnreadBadgeSubscriber"/> to the session's child-event stream and
///     mirrors its count into bindable properties; Dispose detaches. Push-only:
///     no timer, no sampling. A null stream (headless hosts) attaches nothing -
///     nothing UI-related is constructed. Threading contract: events arrive on
///     stream threads, so the caller supplies a marshal wrapper (see Attach) that
///     runs the state update on the UI thread (the shell posts through the
///     Dispatcher; tests pass no wrapper). The badge state itself always flows
///     through the view-model's Apply - the marshal is about threads, never truth.</summary>
internal sealed partial class TabBadgeViewModel : ObservableObject, IDisposable
{
  /// <summary>Attaches the badge to a session's child-event stream. Null stream:
  ///     returns null - headless hosts construct nothing.</summary>
  public static TabBadgeViewModel? Attach(IAgentEvents? events, AgentId rootId,
      Func<Action<int>, Action<int>>? marshal = null)
  {
    if (events is null)
    {
      return null;
    }

    Func<Action<int>, Action<int>> wrap = marshal ?? (apply => apply);
    UnreadBadgeSubscriber subscriber = new(rootId, _ => { });
    TabBadgeViewModel badge = new(events.Subscribe(subscriber));
    subscriber.ChangeSink(wrap(badge.Apply));
    return badge;
  }

  private TabBadgeViewModel(IDisposable lease) => _lease = lease;

  private readonly IDisposable _lease;

  /// <summary>The unread count (badge text; hidden when zero).</summary>
  [ObservableProperty]
  public partial int UnreadCount { get; set; }

  /// <summary>Whether the badge shows at all.</summary>
  public bool HasUnread => UnreadCount > 0;

  /// <summary>The state update - called through the caller's marshal wrapper.</summary>
  private void Apply(int count)
  {
    UnreadCount = count;
    OnPropertyChanged(nameof(HasUnread));
  }

  public void Dispose() => _lease.Dispose();
}
