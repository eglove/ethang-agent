using System.Reflection;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using eThangAgent.Composition;
using eThangAgent.Desktop.ViewModels;
using eThangAgent.Desktop.Views;
using eThangAgent.ModelDomain;
using eThangAgent.SharedKernel;
using eThangAgent.Storage.ACL;
using Microsoft.Extensions.DependencyInjection;

namespace eThangAgent.Desktop.Tests;

/// <summary>The launch dialog's guard rails. The 2026-10-20 crash: the Open
///     button was ALWAYS enabled (the command carried no canExecute, so the
///     view-model's CanOpen was never consulted), its lambda suppressed the
///     null warning with a null-forgiving operator, and the shell applied the
///     choice with Path.GetFullPath and no validation. Pressing Open with no
///     workspace chosen therefore threw ArgumentNullException inside an
///     async-void dispatcher handler and took the whole process down. These
///     tests pin the three layers that make that impossible: the command's
///     availability, the command's own guard, and the shell's refusal.</summary>
public sealed class NewAgentLaunchGuardTests
{
  private static readonly IReadOnlyList<ProviderOption> Options =
      [new("openrouter", "OpenRouter")];

  /// <summary>Every window the shell shows, in order. Avalonia keeps no global
  ///     window list, so the class handler is the only way to observe the
  ///     dialog the production handler shows.</summary>
  private readonly List<Window> _shown = [];

  public NewAgentLaunchGuardTests() =>
      _ = Window.WindowOpenedEvent.AddClassHandler<Window>((w, _) => _shown.Add(w));

  /// <summary>Runs the production new-agent handler through the real shell
  ///     wiring. It is private, so reflection reaches it - the same shape the
  ///     settings-dialog test uses.</summary>
  private static Task RunNewAgentDialogAsync(MainWindow window)
  {
    MethodInfo open = typeof(MainWindow).GetMethod("ShowNewAgentDialogAsync",
        BindingFlags.NonPublic | BindingFlags.Instance)
        ?? throw new InvalidOperationException("MainWindow must expose ShowNewAgentDialogAsync.");
    return (Task)open.Invoke(window, [])!;
  }

  private static NewAgentViewModel CreateDialog() => new(Options, "openrouter");

  // ── the Open command's availability ──

  [Fact]
  public void OpenCommand_Is_Not_Executable_Without_A_Workspace()
  {
    NewAgentViewModel vm = CreateDialog();

    Assert.False(vm.OpenCommand.CanExecute(null));

    vm.WorkspaceRoot = "   "; // a blank root is not a root either

    Assert.False(vm.OpenCommand.CanExecute(null));
  }

  [Fact]
  public void OpenCommand_Is_Not_Executable_Without_A_Provider()
  {
    NewAgentViewModel vm = CreateDialog();
    vm.SelectedProvider = null;

    Assert.False(vm.OpenCommand.CanExecute(null));
  }

  [Fact]
  public void OpenCommand_Is_Executable_Once_A_Provider_And_A_Workspace_Are_Chosen()
  {
    NewAgentViewModel vm = CreateDialog();

    vm.SetWorkspaceRoot(@"C:\work\demo");

    Assert.True(vm.OpenCommand.CanExecute(null));
  }

  [Fact]
  public void OpenCommand_Executed_Without_A_Workspace_Raises_Nothing()
  {
    NewAgentViewModel vm = CreateDialog();
    NewAgentChoice? captured = null;
    vm.OpenRequested += (_, choice) => captured = choice;

    // ICommand.Execute does NOT consult CanExecute (the toolkit's RelayCommand
    // runs the action regardless), so the action itself must refuse a missing
    // workspace - the guard in the action is load-bearing, not belt-and-braces.
    vm.OpenCommand.Execute(null);

    Assert.Null(captured);
  }

  [Fact]
  public void OpenCommand_Availability_Notifies_When_The_Workspace_Changes()
  {
    NewAgentViewModel vm = CreateDialog();
    int notifications = 0;
    vm.OpenCommand.CanExecuteChanged += (_, _) => notifications++;

    vm.SetWorkspaceRoot(@"C:\work\demo");

    // The button must be requeried when the workspace arrives, or it stays
    // greyed out forever in the real dialog.
    Assert.True(notifications > 0,
        "the Open button's availability must be requeried when the workspace changes.");
  }

  // ── the shell's launch application ──

  [Fact]
  public async Task ApplyLaunchFiles_With_A_Null_Workspace_Root_Does_Not_Throw()
  {
    GuardPreferenceStore store = new();
    MainViewModel shell = CreateShell(store);

    Exception? failure = await Record.ExceptionAsync(() => shell.ApplyLaunchFilesAsync(
        new NewAgentChoice("openrouter", null!, [], [], [], []))).ConfigureAwait(true);

    Assert.Null(failure);
  }

  [Fact]
  public async Task ApplyLaunchFiles_With_A_Null_Workspace_Root_Persists_Nothing()
  {
    GuardPreferenceStore store = new();
    MainViewModel shell = CreateShell(store);

    Result<bool> applied = await shell.ApplyLaunchFilesAsync(
        new NewAgentChoice("openrouter", null!, [], [], [], []));

    Assert.Empty(store.Writes);
    Assert.Empty(store.Deletions);
    // A NAMED failure, not an exception: the shell refuses the launch and
    // says why in a code the UI can surface.
    Assert.False(applied.IsSuccess);
    Assert.Equal("InvalidWorkspace", applied.Error.Code);
  }

  [Fact]
  public async Task ApplyLaunchFiles_With_A_Blank_Workspace_Root_Persists_Nothing()
  {
    GuardPreferenceStore store = new();
    MainViewModel shell = CreateShell(store);

    Result<bool> applied = await shell.ApplyLaunchFilesAsync(
        new NewAgentChoice("openrouter", "   ", [], [], [], []));

    // A blank root must be refused BEFORE any preference is written or deleted -
    // otherwise the workspace-scoped keys are derived from a garbage path.
    Assert.Empty(store.Writes);
    Assert.Empty(store.Deletions);
    Assert.False(applied.IsSuccess);
    Assert.Equal("InvalidWorkspace", applied.Error.Code);
  }

  // ── the dialog's chrome: the Open button is DISABLED until the choice is
  //    complete, so the crash is unreachable by clicking (not only by the
  //    programmatic path the command test covers) ──

  [AvaloniaFact]
  public void OpenButton_Is_Disabled_Until_A_Workspace_Is_Chosen()
  {
    NewAgentWindow window = new(Options, "openrouter");
    window.Show();
    Dispatcher.UIThread.RunJobs();

    Button open = window.GetControl<Button>("OpenButton");

    // IsEffectivelyEnabled is the button's REAL state: Avalonia folds the
    // command's CanExecute into IsEnabledCore, and the effective state is
    // what greys the button out. The local IsEnabled property stays true.
    Assert.False(open.IsEffectivelyEnabled);

    NewAgentViewModel vm = (NewAgentViewModel)window.DataContext!;
    vm.SetWorkspaceRoot(@"C:\work\demo");
    Dispatcher.UIThread.RunJobs();

    Assert.True(open.IsEffectivelyEnabled);
  }

  // ── the crash amplifier: the dialog handler is an async-void on the
  //    dispatcher, so an escaping exception took the WHOLE process down. The
  //    handler must never fault, whatever the choice carries. ──

  [AvaloniaFact]
  public async Task NewAgentDialog_Handler_Does_Not_Fault_On_A_Null_Workspace()
  {
    GuardPreferenceStore store = new();
    MainViewModel shell = CreateShell(store);
    MainWindow window = new(shell);
    window.Show();
    Dispatcher.UIThread.RunJobs();

    // The production handler, through the real shell wiring: it shows the
    // dialog and applies the choice. The dialog is closed with a choice that
    // carries NO workspace - the exact shape that crashed the app.
    Task open = RunNewAgentDialogAsync(window);
    Dispatcher.UIThread.RunJobs();

    NewAgentWindow? dialog = _shown.OfType<NewAgentWindow>().LastOrDefault();
    Assert.NotNull(dialog);
    dialog.Close(new NewAgentChoice("openrouter", null!, [], [], [], []));
    Dispatcher.UIThread.RunJobs();

    // The refusal SURFACES to the user: the shell shows the named error in a
    // dialog (that is what the handler awaits now). The dialog is modal, so
    // the handler completes only after it is dismissed - close it, then the
    // handler must complete WITHOUT faulting.
    Window? failure = _shown.LastOrDefault(w => w is not NewAgentWindow);
    Assert.NotNull(failure);
    failure.Close();
    Dispatcher.UIThread.RunJobs();

    await open.WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken).ConfigureAwait(true);

    // The handler completed without faulting - the process is intact, and the
    // launch was REFUSED (nothing was persisted under a garbage root).
    Assert.False(open.IsFaulted, open.Exception?.ToString());
    Assert.Empty(store.Writes);
    Assert.Empty(store.Deletions);
  }

  // ── an UNRESOLVABLE (non-blank) root: the shape that still reaches
  //    Path.GetFullPath, which THROWS rather than returning a failure. The
  //    shell must refuse it with a named error, and the open path must
  //    never run (OpenAgentAsync would throw on the same path). ──

  [Fact]
  public async Task ApplyLaunchFiles_With_An_Unresolvable_Workspace_Root_Fails_Named()
  {
    GuardPreferenceStore store = new();
    MainViewModel shell = CreateShell(store);

    Result<bool> applied = await shell.ApplyLaunchFilesAsync(
        new NewAgentChoice("openrouter", "C:\\bad\0path", [], [], [], []));

    Assert.False(applied.IsSuccess);
    Assert.Equal("InvalidWorkspace", applied.Error.Code);
    Assert.Empty(store.Writes);
    Assert.Empty(store.Deletions);
  }

  [Fact]
  public async Task OpenAgent_With_An_Unresolvable_Workspace_Root_Fails_Named()
  {
    MainViewModel shell = CreateShell(new GuardPreferenceStore());

    // The open path must be safe on its own too: the launch handler calls it
    // right after the launch files are applied.
    Exception? failure = await Record.ExceptionAsync(() =>
        shell.OpenAgentAsync("C:\\bad\0path", "openrouter")).ConfigureAwait(true);

    Assert.Null(failure);
  }

  // ── helpers ──

  private static MainViewModel CreateShell(IAppPreferenceStore? preferences)
      => new((root, provider) => Task.FromResult(Result.Success(BuildSession(root, provider))),
          new MainViewModelOptions { Preferences = preferences });

  private static AgentSession BuildSession(string root, string provider)
      => new(
          new ServiceCollection().BuildServiceProvider(),
          AgentDomain.AgentId.NewId(),
          new ConversationDomain.Conversation(),
          Handler: null!,
          Lifecycle: new RootSessionLifecycle(new TestFixtures.StubStore()),
          Model: ModelConfig.Create("test/model", null, 128, 0.1f, 8192).Value!,
          WorkspaceRoot: root,
          ProviderName: provider,
          Inbox: new AgentDomain.BoundedAgentMailbox(),
          ChildRuntime: new TestFixtures.StubAgentRuntime());

  /// <summary>Records every preference write and deletion so a test can prove
  ///     that a refused launch touched nothing.</summary>
  private sealed class GuardPreferenceStore : IAppPreferenceStore
  {
    public List<(string Key, string Value)> Writes { get; } = [];

    public List<string> Deletions { get; } = [];

    public Dictionary<string, string> Stored { get; } = [];

    public Task<string?> GetAsync(string key, CancellationToken ct = default)
        => Task.FromResult(Stored.TryGetValue(key, out string? value) ? value : null);

    public Task<bool> SetAsync(string key, string value, CancellationToken ct = default)
    {
      Writes.Add((key, value));
      Stored[key] = value;
      return Task.FromResult(true);
    }

    public Task<bool> DeleteAsync(string key, CancellationToken ct = default)
    {
      Deletions.Add(key);
      _ = Stored.Remove(key);
      return Task.FromResult(true);
    }
  }
}
