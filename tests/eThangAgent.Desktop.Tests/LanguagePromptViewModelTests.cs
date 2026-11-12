using System.Text.RegularExpressions;
using eThangAgent.AgentDomain;
using eThangAgent.Composition;
using eThangAgent.ConversationDomain;
using eThangAgent.Desktop.ViewModels;
using eThangAgent.Lsp.ACL;
using eThangAgent.SharedKernel;
using eThangAgent.Storage.ACL;
using eThangAgent.TestSupport;
using eThangAgent.ToolDomain;

namespace eThangAgent.Desktop.Tests;

/// <summary>The per-workspace language-server prompt: it names the languages this repository
///     holds and this machine cannot serve, appears once per workspace per session, and
///     installs or dismisses from itself. Nothing here knows Avalonia exists.</summary>
public partial class LanguagePromptViewModelTests
{
  /// <summary>An in-memory preference store: the prompt reads its opt-out through it.</summary>
  private sealed class MemoryPreferences : IAppPreferenceStore
  {
    public Dictionary<string, string> Values { get; } = [with(StringComparer.Ordinal)];

    public Task<string?> GetAsync(string key, CancellationToken ct = default) =>
        Task.FromResult(Values.TryGetValue(key, out string? value) ? value : null);

    public Task<bool> SetAsync(string key, string value, CancellationToken ct = default)
    {
      Values[key] = value;
      return Task.FromResult(true);
    }

    public Task<bool> DeleteAsync(string key, CancellationToken ct = default) =>
        Task.FromResult(Values.Remove(key));
  }

  /// <summary>An installer that records what it was asked to install and reports the
  ///     outcome it was given, so a prompt install is exercised without a network or a
  ///     toolchain.</summary>
  private sealed class ScriptedInstaller(LspInstallOutcome outcome = LspInstallOutcome.Installed)
      : ILanguageServerInstaller
  {
    private readonly List<string> _installed = [];

    public LspInstallOutcome Outcome { get; } = outcome;

    public List<string> Requested { get; } = [];

    public string InstallRoot => "/tmp/ethang-lsp";

    public Task<LspInstallResult> InstallAsync(string languageId, CancellationToken ct)
    {
      Requested.Add(languageId);
      if (Outcome == LspInstallOutcome.Installed)
      {
        _installed.Add(languageId);
      }

      return Task.FromResult(new LspInstallResult(Outcome,
          LanguageServerCatalog.Default.ForLanguage(languageId),
          Outcome == LspInstallOutcome.Installed ? null : "the download failed."));
    }

    public Task RemoveAsync(string languageId, CancellationToken ct) => Task.CompletedTask;

    public IReadOnlyList<string> InstalledLanguageIds() => _installed;

    public string? InstalledVersion(string languageId) =>
        _installed.Contains(languageId)
            ? LanguageServerCatalog.Default.ForLanguage(languageId)?.Version
            : null;

    public Task RefreshAsync(CancellationToken ct) => Task.CompletedTask;

    public string? MissingPrerequisiteExecutable(string languageId) => null;
  }

  /// <summary>A watcher the test announces on, so a change that adds a language is drivable
  ///     without touching the file system.</summary>
  private sealed class ScriptedWatcher : IFileWatcher
  {
    private readonly List<Func<FileChangeAnnouncement, Task>> _handlers = [];

    public long Generation => 0;

    public IDisposable Subscribe(Func<FileChangeAnnouncement, Task> handler)
    {
      _handlers.Add(handler);
      return new Unsubscriber(_handlers, handler);
    }

    public async Task AnnounceAsync()
    {
      FileChangeAnnouncement announcement = new(["src/new.rs"], [], 1, DateTimeOffset.UnixEpoch);
      foreach (Func<FileChangeAnnouncement, Task> handler in _handlers.ToArray())
      {
        await handler(announcement).ConfigureAwait(false);
      }
    }

    private sealed class Unsubscriber(List<Func<FileChangeAnnouncement, Task>> handlers,
        Func<FileChangeAnnouncement, Task> handler) : IDisposable
    {
      public void Dispose() => handlers.Remove(handler);
    }
  }

  /// <summary>Detection over a scripted answer, so a test states what the workspace holds.
  ///     The answer is replaced by the test to model a repository that gains a language.</summary>
  private sealed class ScriptedDetector(params string[] languages) : IWorkspaceLanguageDetector
  {
    public List<string> Languages { get; } = [.. languages];

    public int Passes { get; private set; }

    public IReadOnlyList<DetectedLanguage> Detect(string workspaceRoot)
    {
      Passes++;
      return [.. Languages.Select(languageId => new DetectedLanguage(languageId, 1, false))];
    }
  }

  private static LanguagePromptViewModel Prompt(ScriptedInstaller installer,
      IAppPreferenceStore? preferences = null, Func<string, Task>? openPage = null) =>
      new(LanguageServerCatalog.Default, installer, preferences, openPage);

  private static Task Present(LanguagePromptViewModel prompt, params string[] detected) =>
      prompt.Present([.. detected.Select(languageId => new DetectedLanguage(languageId, 1, false))],
          []);

  [Fact]
  public async Task AWorkspaceWhoseLanguagesAreAllInstalled_ShowsNoPrompt()
  {
    LanguagePromptViewModel prompt = Prompt(new ScriptedInstaller());

    await prompt.Present([new DetectedLanguage("csharp", 12, true)], ["csharp"],
        TestContext.Current.CancellationToken);

    Assert.False(prompt.IsVisible);
    Assert.Equal(string.Empty, prompt.Message);
  }

  [Fact]
  public async Task OneMissingLanguage_IsNamed()
  {
    LanguagePromptViewModel prompt = Prompt(new ScriptedInstaller());

    await Present(prompt, "rust");

    Assert.True(prompt.IsVisible);
    Assert.Equal("Rust support available", prompt.Message);
  }

  [Fact]
  public async Task ThreeMissingLanguages_AreNamedInOneSentence()
  {
    LanguagePromptViewModel prompt = Prompt(new ScriptedInstaller());

    await Present(prompt, "rust", "typescript", "python");

    Assert.True(prompt.IsVisible);
    Assert.Equal("Rust, TypeScript / JavaScript and Python support available", prompt.Message);
  }

  [Fact]
  public async Task TwoMissingLanguages_AreJoinedWithAnd()
  {
    LanguagePromptViewModel prompt = Prompt(new ScriptedInstaller());

    await Present(prompt, "rust", "python");

    Assert.Equal("Rust and Python support available", prompt.Message);
  }

  [Fact]
  public async Task ADismissedPrompt_DoesNotReturnInTheSameSession()
  {
    LanguagePromptViewModel prompt = Prompt(new ScriptedInstaller());
    await Present(prompt, "rust");

    prompt.DismissCommand.Execute(null);
    await Present(prompt, "rust");

    Assert.False(prompt.IsVisible);
  }

  [Fact]
  public async Task ThePrompt_AppearsOncePerWorkspace_NotOnEveryDetection()
  {
    LanguagePromptViewModel prompt = Prompt(new ScriptedInstaller());
    await Present(prompt, "rust");
    prompt.DismissCommand.Execute(null);

    await Present(prompt, "rust", "python");

    Assert.True(prompt.IsVisible);
    Assert.Equal("Rust and Python support available", prompt.Message);
  }

  [Fact]
  public async Task InstallAll_InstallsEveryNamedLanguage_AndThePromptGoesAway()
  {
    ScriptedInstaller installer = new();
    LanguagePromptViewModel prompt = Prompt(installer);
    await Present(prompt, "rust", "python");

    await prompt.InstallAllCommand.ExecuteAsync(null);

    Assert.Equal(["rust", "python"], installer.Requested);
    Assert.False(prompt.IsVisible);
    Assert.Equal(string.Empty, prompt.Message);
  }

  [Fact]
  public async Task InstallAll_NamesTheLanguageItIsInstalling()
  {
    List<string> seen = [];
    ScriptedInstaller installer = new();
    LanguagePromptViewModel prompt = Prompt(installer);
    await Present(prompt, "rust", "python");
    prompt.PropertyChanged += (_, e) =>
    {
      if (e.PropertyName == nameof(LanguagePromptViewModel.Message) && prompt.Message.StartsWith("Installing", StringComparison.Ordinal))
      {
        seen.Add(prompt.Message);
      }
    };

    await prompt.InstallAllCommand.ExecuteAsync(null);

    Assert.Equal(["Installing Rust\u2026", "Installing Python\u2026"], seen);
  }

  [Fact]
  public async Task InstallAll_ReportsTheFailure_AndKeepsThePrompt()
  {
    LanguagePromptViewModel prompt = Prompt(new ScriptedInstaller(LspInstallOutcome.DownloadFailed));
    await Present(prompt, "rust");

    await prompt.InstallAllCommand.ExecuteAsync(null);

    Assert.True(prompt.IsVisible);
    Assert.Contains("Rust", prompt.Message, StringComparison.Ordinal);
    Assert.Contains("failed", prompt.Message, StringComparison.Ordinal);
  }

  [Fact]
  public async Task Choose_OpensThePage_AndDismisses()
  {
    List<string> opened = [];
    LanguagePromptViewModel prompt = Prompt(new ScriptedInstaller(), openPage: languageId =>
    {
      opened.Add(languageId);
      return Task.CompletedTask;
    });
    await Present(prompt, "rust");

    await prompt.ChooseCommand.ExecuteAsync(null);

    Assert.Equal(["rust"], opened);
    Assert.False(prompt.IsVisible);
  }

  [Fact]
  public async Task TheOptOutPreference_SuppressesThePrompt()
  {
    MemoryPreferences preferences = new();
    preferences.Values[LanguageServerPreferences.PromptDisabledKey] = "true";
    LanguagePromptViewModel prompt = Prompt(new ScriptedInstaller(), preferences);

    await Present(prompt, "rust");

    Assert.False(prompt.IsVisible);
  }

  [Fact]
  public async Task ASessionInARustWorkspace_PromptsOnce_AndAWatcherChangeThatAddsGoPromptsAgain()
  {
    ScriptedDetector detector = new("rust");
    ScriptedWatcher watcher = new();
    ScriptedInstaller installer = new();
    AgentSessionViewModel vm = Session(detector, installer, watcher);

    await WaitForAsync(() => vm.LanguagePrompt?.IsVisible == true);
    Assert.Equal("Rust support available", vm.LanguagePrompt!.Message);

    vm.LanguagePrompt.DismissCommand.Execute(null);
    detector.Languages.Add("go");
    await watcher.AnnounceAsync();

    await WaitForAsync(() => vm.LanguagePrompt?.IsVisible == true);
    Assert.Equal("Rust and Go support available", vm.LanguagePrompt.Message);
  }

  /// <summary>A language the stored preference already names is not offered, over an
  ///     installer that has not been refreshed: detection names TypeScript, the store says it
  ///     is installed, and the prompt stays hidden.</summary>
  [Fact]
  public async Task ALanguageTheStoredPreferenceNames_IsNotPromptedFor()
  {
    MemoryPreferences preferences = new();
    preferences.Values[LanguageServerPreferences.InstalledKey] = LanguageServerPreferences.Serialize(
        [new InstalledLanguageServer("typescript", LspServerCatalog.ForLanguage("typescript")!.Version)]);
    LanguageServerInstaller installer = new(LanguageServerCatalog.Default, preferences, TestInstallRoot());
    ScriptedDetector detector = new("typescript");
    AgentSessionViewModel vm = Session(detector, installer, new ScriptedWatcher());

    await vm.LanguagePass.ConfigureAwait(true);

    Assert.Equal(1, detector.Passes);
    Assert.False(vm.LanguagePrompt!.IsVisible);
    Assert.Equal(string.Empty, vm.LanguagePrompt.Message);
  }

  [Fact]
  public void ASessionWithoutLanguageSeams_HasNoPrompt()
  {
    AgentSessionViewModel vm = TestFixtures.CreateViewModel();

    Assert.Null(vm.LanguagePrompt);
  }

  [Fact]
  public void NoPartOfTheAppDisplaysLanguageServerStatusInAStatusBar()
  {
    string view = File.ReadAllText(Path.Combine(RepoRoot(), "src", "eThangAgent.Desktop", "Views", "AgentView.axaml"));
    string status = File.ReadAllText(Path.Combine(RepoRoot(), "src", "eThangAgent.Desktop", "ViewModels", "StatusViewModel.cs"));

    Assert.DoesNotContain("ServerStatusText", view, StringComparison.Ordinal);
    Assert.DoesNotContain("ServerDisplay", view, StringComparison.Ordinal);
    Assert.DoesNotContain("ServerBreakdownText", view, StringComparison.Ordinal);
    Assert.DoesNotMatch(ServerSegmentPattern(), status);
  }

  [GeneratedRegex(@"Server(Display|BreakdownText|StatusText)")]
  private static partial Regex ServerSegmentPattern();

  private static string RepoRoot()
  {
    DirectoryInfo? dir = new(typeof(LanguagePromptViewModelTests).Assembly.Location);
    while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "eThangAgent.slnx")))
    {
      dir = dir.Parent;
    }

    return dir!.FullName;
  }

  /// <summary>An install root inside the test's own temp tree: nothing in these tests
  ///     installs, so no directory is created, and nothing outside it is ever touched.</summary>
  private static string TestInstallRoot() =>
      Path.Combine(TestTempRoot.Root, "ethang-lsp-prompt-tests");

  /// <summary>Waits for a condition the session reaches on a pool thread, bounded by the
  ///     suite's own ceiling so a broken prompt fails instead of hanging.</summary>
  private static async Task WaitForAsync(Func<bool> condition)
  {
    DateTime deadline = DateTime.UtcNow + TestCeiling.Value;
    while (!condition())
    {
      Assert.True(DateTime.UtcNow < deadline, "the prompt never reached the expected state.");
      await Task.Delay(10, TestContext.Current.CancellationToken).ConfigureAwait(true);
    }
  }

  /// <summary>A session over the scripted seams, with a synchronous UI marshal so the
  ///     detection pass lands headless.</summary>
  private static AgentSessionViewModel Session(IWorkspaceLanguageDetector detector,
      ILanguageServerInstaller installer, IFileWatcher watcher) =>
      new((command, ct, callbacks, onNotice) => Task.FromResult(Result.Success("ack")),
          new RootSessionLifecycle(new TestFixtures.StubStore()),
          AgentId.NewId(),
          new Conversation(),
          provider: "OpenRouter",
          modelId: "test/model",
          new AgentSessionViewModelOptions
          {
            WorkspaceRoot = @"C:\work\demo",
            UiMarshal = action => action(),
            LanguageCatalog = LanguageServerCatalog.Default,
            LanguageInstaller = installer,
            LanguageDetector = detector,
            WorkspaceWatcher = watcher,
          });
}
