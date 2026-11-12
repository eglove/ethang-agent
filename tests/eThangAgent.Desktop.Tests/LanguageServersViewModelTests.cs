using eThangAgent.Composition;
using eThangAgent.Desktop.ViewModels;
using eThangAgent.Lsp.ACL;
using eThangAgent.Storage.ACL;
using eThangAgent.TestSupport;

namespace eThangAgent.Desktop.Tests;

/// <summary>The language-servers page: every language listed always, one status per row, and
///     the install / remove commands. The catalog, the installer and detection are the
///     composition seams, so nothing here knows Avalonia exists.</summary>
public class LanguageServersViewModelTests
{
  /// <summary>An in-memory preference store: the page reads and writes install state through
  ///     it, and one instance shared by two pages proves a restart reads the value back.</summary>
  private sealed class MemoryPreferences : IAppPreferenceStore
  {
    private readonly Dictionary<string, string> _values = [with(StringComparer.Ordinal)];

    public Task<string?> GetAsync(string key, CancellationToken ct = default) =>
        Task.FromResult(_values.TryGetValue(key, out string? value) ? value : null);

    public Task<bool> SetAsync(string key, string value, CancellationToken ct = default)
    {
      _values[key] = value;
      return Task.FromResult(true);
    }

    public Task<bool> DeleteAsync(string key, CancellationToken ct = default) =>
        Task.FromResult(_values.Remove(key));
  }

  /// <summary>A strategy that installs the row's entry point on disk and reports the outcome
  ///     it was given, so a page install is exercised without a network or a toolchain.</summary>
  private sealed class ScriptedStrategy(LspInstallOutcome outcome) : ILspInstallStrategy
  {
    public int Runs { get; private set; }

    public async Task<LspInstallResult> InstallAsync(LspLanguageRow row, string installRoot, CancellationToken cancellationToken)
    {
      Runs++;
      if (outcome == LspInstallOutcome.Installed)
      {
        string directory = row.InstallKind == LspInstallKind.Toolchain
            ? Path.Combine(installRoot, row.LanguageId, row.Version)
            : Path.Combine(installRoot, row.PackageId, row.Version);
        _ = Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(Path.Combine(directory, row.ServerFileName), string.Empty, cancellationToken)
            .ConfigureAwait(false);
      }

      return new LspInstallResult(outcome, row);
    }

    public string? ResolveInstalled(LspLanguageRow row, string installRoot) => null;
  }

  private static LanguageServerInstaller Installer(
      IAppPreferenceStore preferences, string root, LspInstallOutcome outcome, out ScriptedStrategy strategy,
      bool prerequisitePresent = true)
  {
    ScriptedStrategy scripted = new(outcome);
    strategy = scripted;
    return new LanguageServerInstaller(
        LanguageServerCatalog.Default,
        preferences,
        root,
        new Dictionary<LspInstallKind, ILspInstallStrategy>
        {
          [LspInstallKind.NuGet] = scripted,
          [LspInstallKind.Archive] = scripted,
          [LspInstallKind.Toolchain] = scripted,
        },
        new LspPrerequisiteProbe(name => prerequisitePresent
            ? PrerequisiteProbeResult.Found(Path.Combine(root, name + ".exe"))
            : PrerequisiteProbeResult.Missing));
  }

  private static string NewRoot() => TestTempRoot.CreateDirectory("ethang-lsp-page").FullName;

  [Fact]
  public async Task ThePageListsAllTenLanguages_WhateverThePrerequisites()
  {
    string root = NewRoot();
    LanguageServerInstaller installer = Installer(new MemoryPreferences(), root,
        LspInstallOutcome.Installed, out _, prerequisitePresent: false);
    LanguageServersViewModel vm = new(LanguageServerCatalog.Default, installer,
        WorkspaceLanguageDetector.Default);

    await vm.RefreshAsync();

    Assert.Equal(10, vm.Rows.Count);
    Assert.Equal(
        ["C#", "TypeScript / JavaScript", "Python", "Rust", "Go", "Java", "Lua", "PHP", "Ruby", "Bash"],
        vm.Rows.Select(row => row.DisplayName));
    Assert.Equal(7, vm.Rows.Count(row => row.Status == LanguageServerStatus.NeedsPrerequisite));
    Assert.Equal(2, vm.Rows.Count(row => row.Status == LanguageServerStatus.NotInstalled));
  }

  [Fact]
  public async Task AMissingPrerequisiteNamesTheExecutableVerbatim()
  {
    string root = NewRoot();
    LanguageServerInstaller installer = Installer(new MemoryPreferences(), root,
        LspInstallOutcome.Installed, out _, prerequisitePresent: false);
    LanguageServersViewModel vm = new(LanguageServerCatalog.Default, installer,
        WorkspaceLanguageDetector.Default);

    await vm.RefreshAsync();

    LanguageServerRowViewModel java = vm.Rows.Single(row => row.LanguageId == "java");
    Assert.Equal(LanguageServerStatus.NeedsPrerequisite, java.Status);
    Assert.Equal("Needs java (not found)", java.StatusDetail);
    LanguageServerRowViewModel typescript = vm.Rows.Single(row => row.LanguageId == "typescript");
    Assert.Equal("Needs node (not found)", typescript.StatusDetail);
  }

  [Fact]
  public async Task TheCSharpRowIsInstalled_AndItsInstallCommandCannotExecute()
  {
    string root = NewRoot();
    LanguageServerInstaller installer = Installer(new MemoryPreferences(), root,
        LspInstallOutcome.Installed, out ScriptedStrategy strategy);
    LanguageServersViewModel vm = new(LanguageServerCatalog.Default, installer,
        WorkspaceLanguageDetector.Default);

    await vm.RefreshAsync();
    LanguageServerRowViewModel csharp = vm.Rows.Single(row => row.LanguageId == "csharp");

    Assert.Equal(LanguageServerStatus.Installed, csharp.Status);
    Assert.False(csharp.InstallCommand.CanExecute(null));
    await csharp.InstallCommand.ExecuteAsync(null);
    Assert.Equal(0, strategy.Runs);
    Assert.Equal(LanguageServerStatus.Installed, csharp.Status);
  }

  [Fact]
  public async Task Install_PersistsTheLanguageAndVersion_AndANewPageReadsItBackAsInstalled()
  {
    MemoryPreferences preferences = new();
    string root = NewRoot();
    LanguageServerInstaller first = Installer(preferences, root, LspInstallOutcome.Installed, out _);
    LanguageServersViewModel page = new(LanguageServerCatalog.Default, first, WorkspaceLanguageDetector.Default);
    await page.RefreshAsync();
    LanguageServerRowViewModel rust = page.Rows.Single(row => row.LanguageId == "rust");
    Assert.Equal(LanguageServerStatus.NotInstalled, rust.Status);

    await rust.InstallCommand.ExecuteAsync(null);

    Assert.Equal(LanguageServerStatus.Installed, rust.Status);
    string? stored = await preferences.GetAsync(LanguageServerPreferences.InstalledKey,
        TestContext.Current.CancellationToken);
    Assert.NotNull(stored);
    Assert.Contains("\"languageId\":\"rust\"", stored, StringComparison.Ordinal);
    Assert.Contains("\"version\":\"" + rust.Version + "\"", stored, StringComparison.Ordinal);

    LanguageServerInstaller second = Installer(preferences, root, LspInstallOutcome.Installed, out _);
    LanguageServersViewModel restarted = new(LanguageServerCatalog.Default, second, WorkspaceLanguageDetector.Default);
    await restarted.RefreshAsync();

    Assert.Equal(LanguageServerStatus.Installed,
        restarted.Rows.Single(row => row.LanguageId == "rust").Status);
  }

  [Fact]
  public async Task InstallFailureShowsFailedWithTheReason_AndPersistsNothing()
  {
    MemoryPreferences preferences = new();
    string root = NewRoot();
    LanguageServerInstaller installer = Installer(preferences, root, LspInstallOutcome.DownloadFailed, out _);
    LanguageServersViewModel page = new(LanguageServerCatalog.Default, installer, WorkspaceLanguageDetector.Default);
    await page.RefreshAsync();
    LanguageServerRowViewModel lua = page.Rows.Single(row => row.LanguageId == "lua");

    await lua.InstallCommand.ExecuteAsync(null);

    Assert.Equal(LanguageServerStatus.Failed, lua.Status);
    Assert.False(string.IsNullOrWhiteSpace(lua.StatusDetail));
    Assert.Equal(lua.StatusDetail, page.PageError);
    Assert.Null(await preferences.GetAsync(LanguageServerPreferences.InstalledKey,
        TestContext.Current.CancellationToken));
  }

  [Fact]
  public async Task RemoveDeletesTheDirectory_AndLeavesASiblingLanguageIntact()
  {
    MemoryPreferences preferences = new();
    string root = NewRoot();
    LanguageServerInstaller installer = Installer(preferences, root, LspInstallOutcome.Installed, out _);
    LanguageServersViewModel page = new(LanguageServerCatalog.Default, installer, WorkspaceLanguageDetector.Default);
    await page.RefreshAsync();
    await page.Rows.Single(row => row.LanguageId == "rust").InstallCommand.ExecuteAsync(null);
    await page.Rows.Single(row => row.LanguageId == "lua").InstallCommand.ExecuteAsync(null);
    string rustDirectory = Path.Combine(root, LspServerCatalog.ForLanguage("rust")!.PackageId,
        LspServerCatalog.ForLanguage("rust")!.Version);
    string luaDirectory = Path.Combine(root, LspServerCatalog.ForLanguage("lua")!.PackageId,
        LspServerCatalog.ForLanguage("lua")!.Version);
    Assert.True(Directory.Exists(rustDirectory));

    await page.Rows.Single(row => row.LanguageId == "rust").RemoveCommand.ExecuteAsync(null);

    Assert.False(Directory.Exists(rustDirectory));
    Assert.True(Directory.Exists(luaDirectory));
    Assert.Equal(LanguageServerStatus.NotInstalled,
        page.Rows.Single(row => row.LanguageId == "rust").Status);
  }

  [Fact]
  public async Task AnInstalledVersionBehindThePinShowsUpdateAvailable_AndItsUpdateInstalls()
  {
    MemoryPreferences preferences = new();
    string root = NewRoot();
    string pinned = LspServerCatalog.ForLanguage("lua")!.Version;
    _ = await preferences.SetAsync(LanguageServerPreferences.InstalledKey,
        LanguageServerPreferences.Serialize([new InstalledLanguageServer("lua", "0.0.1")]),
        TestContext.Current.CancellationToken);
    LanguageServerInstaller installer = Installer(preferences, root, LspInstallOutcome.Installed, out _);
    LanguageServersViewModel page = new(LanguageServerCatalog.Default, installer, WorkspaceLanguageDetector.Default);

    await page.RefreshAsync();
    LanguageServerRowViewModel lua = page.Rows.Single(row => row.LanguageId == "lua");

    Assert.Equal(LanguageServerStatus.UpdateAvailable, lua.Status);
    await lua.UpdateCommand.ExecuteAsync(null);
    Assert.Equal(LanguageServerStatus.Installed, lua.Status);
    Assert.Equal(pinned, (await installer.ReadAsync(TestContext.Current.CancellationToken)).Version("lua"));
  }

  [Fact]
  public async Task TheWorkspacePromptReportsOnlyLanguagesThisWorkspaceHoldsAndThisMachineLacks()
  {
    MemoryPreferences preferences = new();
    string root = NewRoot();
    string workspace = Path.Combine(root, "workspace");
    _ = Directory.CreateDirectory(workspace);
    await File.WriteAllTextAsync(Path.Combine(workspace, "main.py"), "print(1)",
        TestContext.Current.CancellationToken);
    await File.WriteAllTextAsync(Path.Combine(workspace, "main.rs"), "fn main() {}",
        TestContext.Current.CancellationToken);
    LanguageServerInstaller installer = Installer(preferences, root, LspInstallOutcome.Installed, out _);
    LanguageServersViewModel page = new(LanguageServerCatalog.Default, installer,
        WorkspaceLanguageDetector.Default, workspace);
    await page.Rows.Single(row => row.LanguageId == "rust").InstallCommand.ExecuteAsync(null);

    // NO REFRESH FIRST: the page has rendered nothing, which is the state a caller that
    // never opened the page is in. The answer must still come from the store.
    IReadOnlyList<string> missing = await page
        .MissingForWorkspaceAsync(TestContext.Current.CancellationToken);

    Assert.Contains("python", missing);
    Assert.DoesNotContain("rust", missing);
    Assert.DoesNotContain("csharp", missing);
  }
}
