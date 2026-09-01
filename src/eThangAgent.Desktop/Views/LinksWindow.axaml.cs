using Avalonia.Controls;
using Avalonia.Interactivity;
using eThangAgent.AgentDomain;
using eThangAgent.Desktop.ViewModels;
using eThangAgent.SharedKernel;

namespace eThangAgent.Desktop.Views;

/// <summary>The Links dialog (consent door, D10): the agents a session may link to
///     and the links it already holds. Confirming registers a CONSENTED link on the
///     session's registry — the same object agent.route resolves names through —
///     so a link created here is immediately routable. The view only owns window
///     mechanics; rows, guards, and state live in the view-model.</summary>
internal partial class LinksWindow : Window
{
  private readonly LinksViewModel? _vm;

  public LinksWindow() => InitializeComponent();

  public LinksWindow(
      Func<CancellationToken, Task<Result<IReadOnlyList<AgentRecord>>>> load,
      AgentLinkRegistry registry) : this()
  {
    _vm = new LinksViewModel(load, registry);
    DataContext = _vm;
    Opened += (_, _) => _ = _vm.LoadAsync();
  }

  private void OnClose(object? sender, RoutedEventArgs e) => Close(null);
}
