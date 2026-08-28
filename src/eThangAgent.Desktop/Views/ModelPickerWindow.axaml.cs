using Avalonia.Controls;
using Avalonia.Interactivity;
using eThangAgent.Desktop.ViewModels;
using eThangAgent.ModelDomain;
using eThangAgent.SharedKernel;

namespace eThangAgent.Desktop.Views;

/// <summary>The model picker modal: a search box over the open session's provider
///     catalog (plus the auto row where the provider offers one). Confirming closes
///     the dialog with the chosen <see cref="ModelChoice"/>; cancelling closes it with
///     null. The view only owns window mechanics — rows, search, and state live in
///     the view-model.</summary>
internal partial class ModelPickerWindow : Window
{
  private readonly ModelPickerViewModel? _vm;

  public ModelPickerWindow() => InitializeComponent();

  public ModelPickerWindow(
      Func<CancellationToken, Task<Result<IReadOnlyList<ModelProviderEntry>>>> loadCatalog,
      bool allowAuto,
      string? currentModelId) : this()
  {
    _vm = new ModelPickerViewModel(loadCatalog, allowAuto, currentModelId);
    DataContext = _vm;
    _vm.ConfirmRequested += (_, choice) => Close(choice);
    // Kick the catalog fetch once the window is up (already on the UI thread): the
    // list shows its loading state until the rows land. LoadAsync keeps failures
    // internal (they land in its error state), so the discarded task is safe.
    Opened += (_, _) => _ = _vm.LoadAsync();
  }

  private void OnCancel(object? sender, RoutedEventArgs e) => Close(null);

  private void OnRowDoubleTapped(object? sender, Avalonia.Input.TappedEventArgs e)
      // Same guard as the Select button: only a selected row confirms.
      => _vm?.ConfirmCommand.Execute(null);
}
