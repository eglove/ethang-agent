using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using eThangAgent.Composition;

namespace eThangAgent.Desktop.ViewModels;

/// <summary>The provider/workspace pair chosen in the new-agent dialog.</summary>
internal sealed record NewAgentChoice(string ProviderId, string WorkspaceRoot);

/// <summary>View-model behind the new-agent modal: an AI-provider dropdown and a
///     workspace chosen through the platform folder picker. The dialog is the only
///     place a session's provider is decided — the workspace tab then stays on that
///     provider until closed. Pure state and commands; the view owns the picker.</summary>
internal sealed partial class NewAgentViewModel : ObservableObject
{
  /// <summary>Raised when the user asks for the platform folder picker. The view
  ///     shows it and feeds the result back through <see cref="SetWorkspaceRoot"/>.</summary>
  public event EventHandler? WorkspaceRequested;

  /// <summary>Raised when the user confirms; carries the validated choice. The view
  ///     closes the dialog with it.</summary>
  public event EventHandler<NewAgentChoice>? OpenRequested;

  public IReadOnlyList<ProviderOption> Providers { get; }

  public ICommand ChooseWorkspaceCommand { get; }

  public ICommand OpenCommand { get; }

  [ObservableProperty]
  [NotifyPropertyChangedFor(nameof(CanOpen))]
  public partial ProviderOption? SelectedProvider { get; set; }

  [ObservableProperty]
  [NotifyPropertyChangedFor(nameof(CanOpen))]
  public partial string? WorkspaceRoot { get; set; }

  /// <summary>Open is only actionable once BOTH a provider and a workspace are chosen.</summary>
  public bool CanOpen => SelectedProvider is not null && !string.IsNullOrWhiteSpace(WorkspaceRoot);

  public NewAgentViewModel(IReadOnlyList<ProviderOption> providers, string preferredProviderId)
  {
    Providers = providers is { Count: > 0 }
        ? providers
        : throw new ArgumentException("At least one configured provider is required.", nameof(providers));
    SelectedProvider = providers.FirstOrDefault(p => p.Id == preferredProviderId) ?? providers[0];
    ChooseWorkspaceCommand = new RelayCommand(() => WorkspaceRequested?.Invoke(this, EventArgs.Empty));
    OpenCommand = new RelayCommand(() =>
        OpenRequested?.Invoke(this, new NewAgentChoice(SelectedProvider!.Id, WorkspaceRoot!)));
  }

  /// <summary>Feeds the folder-picker result back in. An empty pick (cancelled
  ///     dialog) keeps the previous value.</summary>
  public void SetWorkspaceRoot(string? path)
  {
    if (!string.IsNullOrWhiteSpace(path))
    {
      WorkspaceRoot = path;
    }
  }
}
