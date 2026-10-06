using Avalonia.Controls;
using Avalonia.Interactivity;
using eThangAgent.Desktop.ViewModels;

namespace eThangAgent.Desktop.Views;

/// <summary>The MCP servers dialog (issue #106): the configured servers' list with
///     live state, the config form (add/edit), the approval controls, and removal -
///     all through the session's own store and SHARED pooled access, passed in by
///     the shell. The view only owns window mechanics; state and guards live in the
///     view-model.</summary>
internal partial class McpServersWindow : Window
{
  private readonly McpServersViewModel? _vm;

  public McpServersWindow() => InitializeComponent();

  public McpServersWindow(McpServersViewModel vm) : this()
  {
    _vm = vm;
    DataContext = vm;
    Opened += (_, _) => _ = vm.LoadAsync();
  }

  private void OnAdd(object? sender, RoutedEventArgs e) => _vm?.BeginAdd();

  private void OnEdit(object? sender, RoutedEventArgs e)
  {
    if (_vm?.Selected is { } row)
    {
      _vm.BeginEdit(row);
    }
  }

  private void OnCancelForm(object? sender, RoutedEventArgs e) => _vm?.CloseForm();

  private void OnClose(object? sender, RoutedEventArgs e) => Close(null);
}
