using Avalonia.Controls;
using Avalonia.Interactivity;
using eThangAgent.Agent.Application.Sessions;
using eThangAgent.AgentDomain;
using eThangAgent.Desktop.ViewModels;
using eThangAgent.SharedKernel;

namespace eThangAgent.Desktop.Views;

/// <summary>The Sessions dialog: every resumable root session, newest first, with
///     sessions already open in a tab greyed (hover explains why). Confirming closes
///     the dialog with the chosen session id; cancelling closes it with null. The view
///     only owns window mechanics — rows and state live in the view-model.</summary>
internal partial class SessionsWindow : Window
{
  private readonly SessionsViewModel? _vm;

  public SessionsWindow() => InitializeComponent();

  public SessionsWindow(
      Func<CancellationToken, Task<Result<IReadOnlyList<SessionCatalogEntry>>>> load,
      Func<IReadOnlySet<AgentId>> openSessionIds) : this()
  {
    _vm = new SessionsViewModel(load, openSessionIds);
    DataContext = _vm;
    _vm.ConfirmRequested += (_, sessionId) => Close(sessionId);
    // Kick the catalog fetch once the window is up (already on the UI thread): the
    // list shows its loading state until the rows land. LoadAsync keeps failures
    // internal (they land in its error state), so the discarded task is safe.
    Opened += (_, _) => _ = _vm.LoadAsync();
  }

  private void OnCancel(object? sender, RoutedEventArgs e) => Close(null);

  private void OnRowDoubleTapped(object? sender, Avalonia.Input.TappedEventArgs e)
      // Same guard as the Resume button: only a resumable (not already open) row confirms.
      => _vm?.ConfirmCommand.Execute(null);
}
