using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using eThangAgent.Composition;

namespace eThangAgent.Desktop.ViewModels;

/// <summary>The provider/workspace pair chosen in the new-agent dialog.</summary>
internal sealed record NewAgentChoice(string ProviderId, string WorkspaceRoot);

/// <summary>View-model behind the new-agent modal: an AI-provider dropdown and a workspace
///     chosen through the platform folder picker. The file lists this dialog used to edit now
///     live in the workspace settings window (this workspace's session files, skill directories
///     and trusted directories) and in the app Settings window (the ones that apply to every
///     workspace). Pure state and commands; the view owns the picker.</summary>
internal sealed partial class NewAgentViewModel : ObservableObject
{
  /// <summary>Raised when the user asks for the platform folder picker. The view shows it and
  ///     feeds the result back through <see cref="SetWorkspaceRoot"/>.</summary>
  public event EventHandler? WorkspaceRequested;

  /// <summary>Raised when the user confirms; carries the validated choice. The view closes the
  ///     dialog with it.</summary>
  public event EventHandler<NewAgentChoice>? OpenRequested;

  public IReadOnlyList<ProviderOption> Providers { get; }

  public ICommand ChooseWorkspaceCommand { get; }

  /// <summary>The Open command: GATED by <see cref="CanOpen"/>, so the button stays disabled
  ///     until a provider AND a workspace are chosen. The command type is IRelayCommand (not
  ///     ICommand) so the generated change hooks can requery its availability when the provider
  ///     or the workspace changes.</summary>
  public IRelayCommand OpenCommand { get; }

  [ObservableProperty]
  [NotifyPropertyChangedFor(nameof(CanOpen))]
  [NotifyCanExecuteChangedFor(nameof(OpenCommand))]
  public partial ProviderOption? SelectedProvider { get; set; }

  [ObservableProperty]
  [NotifyPropertyChangedFor(nameof(CanOpen))]
  [NotifyCanExecuteChangedFor(nameof(OpenCommand))]
  public partial string? WorkspaceRoot { get; set; }

  /// <summary>Open is only actionable once BOTH a provider and a workspace are chosen.</summary>
  public bool CanOpen => SelectedProvider is not null && !string.IsNullOrWhiteSpace(WorkspaceRoot);

  public NewAgentViewModel(IReadOnlyList<ProviderOption> providers, string preferredProviderId)
  {
    Providers = providers is { Count: > 0 }
        ? providers
        : throw new ArgumentException("At least one configured provider is required.", nameof(providers));

    // The commands exist BEFORE the selected provider is assigned: assigning it raises the
    // generated change hook, which requeries the Open command's availability - a command that
    // does not exist yet would fault there.
    ChooseWorkspaceCommand = new RelayCommand(() => WorkspaceRequested?.Invoke(this, EventArgs.Empty));
    // The Open command is GATED by CanOpen: until a provider AND a workspace are chosen, the
    // button stays disabled. The action's OWN guard is load-bearing too: ICommand.Execute does
    // not consult CanExecute, so a programmatic Execute must not raise a choice over a missing
    // workspace either.
    OpenCommand = new RelayCommand(Open, () => CanOpen);

    SelectedProvider = providers.FirstOrDefault(p => p.Id == preferredProviderId) ?? providers[0];
  }

  /// <summary>The Open action: raises the confirmed choice. It refuses a missing provider or
  ///     workspace by NOT raising the event at all - the dialog stays open, and nothing
  ///     downstream ever sees a half-built choice.</summary>
  private void Open()
  {
    if (SelectedProvider is not { } provider || string.IsNullOrWhiteSpace(WorkspaceRoot))
    {
      return;
    }

    OpenRequested?.Invoke(this, new NewAgentChoice(provider.Id, WorkspaceRoot));
  }

  /// <summary>Feeds the folder-picker result back in. An empty pick (cancelled dialog) keeps
  ///     the previous value.</summary>
  public void SetWorkspaceRoot(string? path)
  {
    if (!string.IsNullOrWhiteSpace(path))
    {
      WorkspaceRoot = path;
    }
  }
}
