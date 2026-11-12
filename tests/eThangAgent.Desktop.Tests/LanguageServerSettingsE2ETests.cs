using eThangAgent.Composition;
using eThangAgent.Desktop.ViewModels;
using eThangAgent.Lsp.ACL;
using eThangAgent.Storage.ACL;
using eThangAgent.TestSupport;
using Microsoft.Extensions.DependencyInjection;

namespace eThangAgent.Desktop.Tests;

/// <summary>The language-servers page end to end, over the REAL composed core: installing from
///     the page persists into the app's own database, a SECOND harness opened over that same
///     database reads the language back as installed with no user action, and removing a
///     language from the page leaves the workspace's other installed languages installed and
///     resolvable.
///     <para>THE PAGE IS BUILT THE WAY THE WINDOW BUILDS IT: from the session container's own
///     catalog, installer, and detector, with the tab's workspace root. The installer is the
///     harness's fake-strategy one, so an install places the in-repo stub server where the real
///     resolver looks and no download happens.</para>
///     <para>THE RESTART REUSES THE DATABASE FILE, which is the whole point of the check: a
///     second temp database would prove nothing about persistence.</para></summary>
[Collection("Desktop E2E")]
[Trait("Category", "Process")]
public class LanguageServerSettingsE2ETests
{
  private static CancellationToken Ct => TestContext.Current.CancellationToken;

  [Fact]
  public async Task InstallingLuaFromThePage_SurvivesAHarnessRestartOverTheSameDatabase()
  {
    string root = TestTempRoot.CreateDirectory("lsp-settings-lua").FullName;
    string database = Path.Combine(TestTempRoot.Root, "ethang-lsp-settings-" + Guid.NewGuid().ToString("N") + ".db");
    try
    {
      _ = Directory.CreateDirectory(Path.Combine(root, "src"));
      await File.WriteAllTextAsync(Path.Combine(root, ".luarc.json"), "{}\n", Ct).ConfigureAwait(true);
      await File.WriteAllTextAsync(Path.Combine(root, "src", "main.lua"), "function greet()\nend\n", Ct).ConfigureAwait(true);

      // The restart: a SECOND harness over the SAME database file, started while the first is
      // still open because a harness DELETES its database file on disposal — a restart after
      // disposal would open an empty database and prove nothing about persistence.
#pragma warning disable CA2007 // 'await using' cannot carry ConfigureAwait.
      await using E2E.HostHarness first = await StartAsync(root, database).ConfigureAwait(true);
      await using E2E.HostHarness restarted = await StartAsync(root, database).ConfigureAwait(true);
#pragma warning restore CA2007
      // Both harnesses run over the one file, so neither deletes it; the run-root sweep does.

      (string status, string detail) = await InstallLuaFromThePageAsync(first).ConfigureAwait(true);
      Assert.Equal("Installed", status);
      Assert.Contains("Installed version", detail, StringComparison.Ordinal);

      LanguageServersViewModel afterRestart = Page(restarted);
      await afterRestart.RefreshAsync().ConfigureAwait(true);

      LanguageServerRowViewModel luaAgain = Row(afterRestart, "lua");
      Assert.Equal(LanguageServerStatus.Installed, luaAgain.Status);
      Assert.Equal("Installed", luaAgain.StatusText);
      Assert.Equal(LspServerCatalog.ForLanguage("lua")!.Version, luaAgain.Data.InstalledVersion);

      ILanguageServerInstaller installer = restarted.Services.GetRequiredService<ILanguageServerInstaller>();
      InstalledLanguageServers installed = await installer.ReadAsync(Ct).ConfigureAwait(true);
      Assert.Contains("lua", installed.LanguageIds, StringComparer.Ordinal);
    }
    finally
    {
      TryDelete(root);
    }
  }

  [Fact]
  public async Task RemovingALanguageFromThePage_LeavesTheWorkspacesOtherLanguageInstalled()
  {
    string root = TestTempRoot.CreateDirectory("lsp-settings-remove").FullName;
    try
    {
      _ = Directory.CreateDirectory(Path.Combine(root, "src"));
      await File.WriteAllTextAsync(Path.Combine(root, ".luarc.json"), "{}\n", Ct).ConfigureAwait(true);
      await File.WriteAllTextAsync(Path.Combine(root, "src", "main.lua"), "function greet()\nend\n", Ct).ConfigureAwait(true);
      await File.WriteAllTextAsync(Path.Combine(root, "Cargo.toml"), "[package]\nname = \"probe\"\n", Ct).ConfigureAwait(true);
      await File.WriteAllTextAsync(Path.Combine(root, "src", "lib.rs"), "fn greet() {}\n", Ct).ConfigureAwait(true);

#pragma warning disable CA2007 // 'await using' cannot carry ConfigureAwait.
      await using E2E.HostHarness host = await StartAsync(root, reuseDatabasePath: null).ConfigureAwait(true);
#pragma warning restore CA2007
      LanguageServersViewModel page = Page(host);
      await page.RefreshAsync().ConfigureAwait(true);

      await Row(page, "lua").InstallCommand.ExecuteAsync(null).ConfigureAwait(true);
      await Row(page, "rust").InstallCommand.ExecuteAsync(null).ConfigureAwait(true);
      Assert.Equal(LanguageServerStatus.Installed, Row(page, "lua").Status);
      Assert.Equal(LanguageServerStatus.Installed, Row(page, "rust").Status);

      LanguageServerRowViewModel lua = Row(page, "lua");
      Assert.True(lua.CanRemove);
      await lua.RemoveCommand.ExecuteAsync(null).ConfigureAwait(true);

      Assert.Equal(LanguageServerStatus.NotInstalled, Row(page, "lua").Status);
      Assert.Equal(LanguageServerStatus.Installed, Row(page, "rust").Status);

      ILanguageServerInstaller installer = host.Services.GetRequiredService<ILanguageServerInstaller>();
      InstalledLanguageServers installed = await installer.ReadAsync(Ct).ConfigureAwait(true);
      Assert.DoesNotContain("lua", installed.LanguageIds, StringComparer.Ordinal);
      Assert.Contains("rust", installed.LanguageIds, StringComparer.Ordinal);

      // The language that stayed installed still has a server on disk, and the one that was
      // removed does not: the removal touched only its own install directory.
      LspServerInstaller resolver = new(LspServerInstaller.DefaultRoot, LspServerInstaller.DefaultStrategies());
      Assert.NotNull(resolver.ResolveInstalled(LspServerCatalog.ForLanguage("rust")!));
      Assert.Null(resolver.ResolveInstalled(LspServerCatalog.ForLanguage("lua")!));
    }
    finally
    {
      TryDelete(root);
    }
  }

  /// <summary>Installs Lua from the page over one harness, and answers the row's status text and
  ///     detail as the page showed them after the install.</summary>
  private static async Task<(string Status, string Detail)> InstallLuaFromThePageAsync(E2E.HostHarness host)
  {
    LanguageServersViewModel page = Page(host);
    await page.RefreshAsync().ConfigureAwait(true);

    LanguageServerRowViewModel lua = Row(page, "lua");
    Assert.Equal(LanguageServerStatus.NotInstalled, lua.Status);
    Assert.True(lua.CanInstall);

    await lua.InstallCommand.ExecuteAsync(null).ConfigureAwait(true);

    Assert.Equal(LanguageServerStatus.Installed, lua.Status);
    Assert.Null(page.PageError);
    return (lua.StatusText, lua.StatusDetail);
  }

  /// <summary>The page as the language-servers window builds it: the session container's own
  ///     three seams and the tab's workspace root.</summary>
  private static LanguageServersViewModel Page(E2E.HostHarness host) =>
      new(
          host.Services.GetRequiredService<ILanguageServerCatalog>(),
          host.Services.GetRequiredService<ILanguageServerInstaller>(),
          host.Services.GetRequiredService<IWorkspaceLanguageDetector>(),
          host.Vm.WorkspaceRoot);

  private static LanguageServerRowViewModel Row(LanguageServersViewModel page, string languageId) =>
      page.Rows.Single(row => string.Equals(row.LanguageId, languageId, StringComparison.OrdinalIgnoreCase));

  private static async Task<E2E.HostHarness> StartAsync(string root, string? reuseDatabasePath)
  {
    E2E.HostHarness host = new();
    host.ClaimHostsForCurrentTest();
    host.LanguageInstallerFactory = Installer;
    host.UiMarshal = action => action();
    host.SharesDatabaseFile = reuseDatabasePath is not null;
    string workspaceId = "lsp-settings-" + Guid.NewGuid().ToString("N");
    return await host.StartAsync(reuseDatabasePath, workspaceRoot: root, workspaceId: workspaceId)
        .ConfigureAwait(true);
  }

  /// <summary>The installer the page reads: the container's own catalog and preference store,
  ///     with an install strategy that places the in-repo stub server where the real resolver
  ///     looks. The install root is the app's own cache directory, the only root the daemon
  ///     reads.</summary>
  private static LanguageServerInstaller Installer(IServiceProvider services) =>
      new(
          services.GetRequiredService<ILanguageServerCatalog>(),
          services.GetRequiredService<IAppPreferenceStore>(),
          LspServerInstaller.DefaultRoot,
          new Dictionary<LspInstallKind, ILspInstallStrategy>
          {
            [LspInstallKind.Archive] = new StubInstallStrategy(),
            [LspInstallKind.Toolchain] = new StubInstallStrategy(),
            [LspInstallKind.NuGet] = new StubInstallStrategy(),
          });

  /// <summary>An install strategy that places the stub server at the row's real install layout
  ///     and reports Installed, so an install from the page needs no network.</summary>
  private sealed class StubInstallStrategy : ILspInstallStrategy
  {
    public Task<LspInstallResult> InstallAsync(LspLanguageRow row, string installRoot, CancellationToken cancellationToken)
    {
      ArgumentNullException.ThrowIfNull(row);
      ArgumentException.ThrowIfNullOrWhiteSpace(installRoot);
      _ = StubLspServer.Install(installRoot, Directory(row), row.EntryPointRelativePath, row.ServerFileName);
      return Task.FromResult(new LspInstallResult(LspInstallOutcome.Installed, row));
    }

    public string? ResolveInstalled(LspLanguageRow row, string installRoot)
    {
      ArgumentNullException.ThrowIfNull(row);
      string candidate = Path.Combine(installRoot, Directory(row),
          row.EntryPointRelativePath.Replace('/', Path.DirectorySeparatorChar), row.ServerFileName);
      return File.Exists(candidate) ? candidate : null;
    }

    /// <summary>The directory segments the production install layout keys one row by: package
    ///     and version for a downloaded row, language and version for a toolchain row.</summary>
    private static string Directory(LspLanguageRow row) =>
        row.InstallKind == LspInstallKind.Toolchain
            ? Path.Combine(row.LanguageId, row.Version)
            : Path.Combine(row.PackageId, row.Version);
  }

  private static void TryDelete(string directory)
  {
    try
    {
      Directory.Delete(directory, recursive: true);
    }
    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
    {
      _ = ex.Message;
    }
  }
}
