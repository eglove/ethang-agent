using eThangAgent.AgentDomain;
using eThangAgent.Lsp.ACL;
using eThangAgent.ModelDomain;
using eThangAgent.SharedKernel;
using eThangAgent.Storage.ACL;
using eThangAgent.TestSupport;
using eThangAgent.ToolDomain;
using Microsoft.Extensions.DependencyInjection;

namespace eThangAgent.Composition.Tests;

/// <summary>The installed-language-server preference: the stored shape, the strict parser, and
///     the installer that writes it. The preference store is the ONLY persistence for install
///     state - there is no installed-languages file, marker file, or cache manifest.
///     <para>The composed-container check builds a real container, which is what earns the
///     Process trait.</para></summary>
[Trait("Category", "Process")]
public class LanguageServerPreferencesTests
{
  /// <summary>A preference store over a throwaway database, so a test never opens the user's
  ///     real one. One instance is shared by two installers to prove a value written by one
  ///     run is read by the next.</summary>
  private sealed class TempPreferenceStore : IAppPreferenceStore, IDisposable
  {
    private readonly string _path;
    private readonly SqliteAppPreferenceStore _inner;

    public TempPreferenceStore()
    {
      _path = Path.Combine(TestTempRoot.Root, $"ethang-lsp-prefs-{Guid.NewGuid():N}.db");
      _inner = new SqliteAppPreferenceStore(new AppDatabase(_path));
    }

    public Task<string?> GetAsync(string key, CancellationToken ct = default) => _inner.GetAsync(key, ct);

    public Task<bool> SetAsync(string key, string value, CancellationToken ct = default) =>
        _inner.SetAsync(key, value, ct);

    public Task<bool> DeleteAsync(string key, CancellationToken ct = default) => _inner.DeleteAsync(key, ct);

    public void Dispose()
    {
      GC.SuppressFinalize(this);
#pragma warning disable CA1031, S108 // Do not catch general exception types
      try
      {
        File.Delete(_path);
      }
      catch
      {
      }
#pragma warning restore CA1031, S108
    }
  }

  /// <summary>A strategy that installs nothing and reports the outcome it was given, so an
  ///     install can be exercised without a network or a toolchain.</summary>
  private sealed class ScriptedStrategy(LspInstallOutcome outcome) : ILspInstallStrategy
  {
    public int Runs { get; private set; }

    public Task<LspInstallResult> InstallAsync(LspLanguageRow row, string installRoot, CancellationToken cancellationToken)
    {
      Runs++;
      if (outcome == LspInstallOutcome.Installed)
      {
        string directory = Path.Combine(installRoot, row.PackageId, row.Version);
        _ = Directory.CreateDirectory(directory);
        using FileStream placeholder = File.Create(Path.Combine(directory, row.ServerFileName));
      }

      return Task.FromResult(new LspInstallResult(outcome, row));
    }

    public string? ResolveInstalled(LspLanguageRow row, string installRoot) => null;
  }

  private static LanguageServerInstaller Installer(
      IAppPreferenceStore preferences, string root, LspInstallOutcome outcome, out ScriptedStrategy strategy)
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
        new LspPrerequisiteProbe(_ => PrerequisiteProbeResult.Missing));
  }

  [Fact]
  public void Parse_ReadsTheStoredShape()
  {
    const string Stored = /*lang=json,strict*/ """
        [{"languageId":"rust","version":"2026-11-09"}]
        """;

    Result<IReadOnlyList<InstalledLanguageServer>> parsed = LanguageServerPreferences.Parse(Stored);

    Assert.True(parsed.IsSuccess);
    InstalledLanguageServer entry = Assert.Single(parsed.Value);
    Assert.Equal("rust", entry.LanguageId);
    Assert.Equal("2026-11-09", entry.Version);
  }

  [Fact]
  public void Parse_AbsentIsAnEmptyList_AndAGarbageValueIsANamedFailure()
  {
    Assert.Empty(LanguageServerPreferences.Parse(null).Value!);
    Assert.Empty(LanguageServerPreferences.Parse("  ").Value!);

    Result<IReadOnlyList<InstalledLanguageServer>> broken = LanguageServerPreferences.Parse("not json");
    Assert.False(broken.IsSuccess);
    Assert.Equal("InvalidInstalledLanguageServers", broken.Error.Code);
  }

  [Fact]
  public void Parse_AnEntryMissingItsVersionIsANamedFailure()
  {
    Result<IReadOnlyList<InstalledLanguageServer>> parsed =
        LanguageServerPreferences.Parse(/*lang=json,strict*/ "[{\"languageId\":\"rust\"}]");

    Assert.False(parsed.IsSuccess);
    Assert.Equal("InvalidInstalledLanguageServers", parsed.Error.Code);
    Assert.Contains("entry 1", parsed.Error.Message, StringComparison.Ordinal);
  }

  [Fact]
  public void Serialize_WritesTheStoredShape_AndRoundTripsThroughParse()
  {
    InstalledLanguageServer[] entries = [new("lua", "3.19.2"), new("rust", "2026-11-09")];

    string stored = LanguageServerPreferences.Serialize(entries);

    Assert.Equal("[{\"languageId\":\"lua\",\"version\":\"3.19.2\"},"
        + "{\"languageId\":\"rust\",\"version\":\"2026-11-09\"}]", stored);
    Result<IReadOnlyList<InstalledLanguageServer>> parsed = LanguageServerPreferences.Parse(stored);
    Assert.True(parsed.IsSuccess);
    Assert.Equal(entries, parsed.Value);
  }

  [Fact]
  public async Task Install_WritesThePreference_AndANewInstallerReadsItBack()
  {
    using TempPreferenceStore preferences = new();
    DirectoryInfo root = TestTempRoot.CreateDirectory("ethang-lsp-install");
    CancellationToken ct = TestContext.Current.CancellationToken;

    LanguageServerInstaller first = Installer(preferences, root.FullName, LspInstallOutcome.Installed, out _);
    LspInstallResult result = await first.InstallAsync("rust", ct);

    Assert.Equal(LspInstallOutcome.Installed, result.Outcome);
    string? stored = await preferences.GetAsync(LanguageServerPreferences.InstalledKey, ct);
    Assert.NotNull(stored);
    Assert.Contains("\"languageId\":\"rust\"", stored, StringComparison.Ordinal);

    LanguageServerInstaller second = Installer(preferences, root.FullName, LspInstallOutcome.Installed, out _);
    await second.RefreshAsync(ct);
    Assert.Equal(["rust"], second.InstalledLanguageIds());
    Assert.Equal("2026-11-09", second.InstalledVersion("rust"));
  }

  [Fact]
  public async Task Install_AFailurePersistsNothing()
  {
    using TempPreferenceStore preferences = new();
    DirectoryInfo root = TestTempRoot.CreateDirectory("ethang-lsp-install-fail");
    CancellationToken ct = TestContext.Current.CancellationToken;
    LanguageServerInstaller installer = Installer(preferences, root.FullName, LspInstallOutcome.DownloadFailed, out _);

    LspInstallResult result = await installer.InstallAsync("lua", ct);

    Assert.Equal(LspInstallOutcome.DownloadFailed, result.Outcome);
    Assert.Null(await preferences.GetAsync(LanguageServerPreferences.InstalledKey, ct));
    Assert.Empty(installer.InstalledLanguageIds());
  }

  [Fact]
  public async Task Install_AnUnknownLanguageIsARefusalNamingIt_NotAnException()
  {
    using TempPreferenceStore preferences = new();
    DirectoryInfo root = TestTempRoot.CreateDirectory("ethang-lsp-install-unknown");
    LanguageServerInstaller installer = Installer(preferences, root.FullName, LspInstallOutcome.Installed,
        out ScriptedStrategy strategy);

    LspInstallResult result = await installer.InstallAsync("nonexistent",
        TestContext.Current.CancellationToken);

    Assert.Equal(LspInstallOutcome.DownloadFailed, result.Outcome);
    Assert.Null(result.Row);
    Assert.Contains("nonexistent", result.Reason, StringComparison.Ordinal);
    Assert.Equal(0, strategy.Runs);
  }

  [Fact]
  public async Task Remove_DeletesOnlyThatLanguagesDirectory_AndClearsItsEntry()
  {
    using TempPreferenceStore preferences = new();
    DirectoryInfo root = TestTempRoot.CreateDirectory("ethang-lsp-remove");
    CancellationToken ct = TestContext.Current.CancellationToken;
    LanguageServerInstaller installer = Installer(preferences, root.FullName, LspInstallOutcome.Installed, out _);

    _ = await installer.InstallAsync("rust", ct);
    _ = await installer.InstallAsync("lua", ct);
    LspLanguageRow rustRow = LspServerCatalog.ForLanguage("rust")!;
    LspLanguageRow luaRow = LspServerCatalog.ForLanguage("lua")!;
    string rustDirectory = Path.Combine(root.FullName, rustRow.PackageId, rustRow.Version);
    string luaDirectory = Path.Combine(root.FullName, luaRow.PackageId, luaRow.Version);
    Assert.True(Directory.Exists(rustDirectory));
    Assert.True(Directory.Exists(luaDirectory));

    await installer.RemoveAsync("rust", ct);

    Assert.False(Directory.Exists(rustDirectory));
    Assert.True(Directory.Exists(luaDirectory),
        "removing one language must leave a sibling's install intact.");
    Assert.Equal(["lua"], installer.InstalledLanguageIds());
  }

  [Fact]
  public void TheThreeSeams_ResolveFromAComposedContainerOverATempDatabase()
  {
    using TestAppDatabase db = TestAppDatabase.Create();
    AgentSettings settings = new(
        new OpenRouterSettings("sk-or-test", new Uri("https://openrouter.test")),
        new AgentRunOptions(null, 2));
    using ServiceProvider services = new ServiceCollection()
        .AddEThangAgentCore(settings, Providers.OpenRouter,
            ModelConfig.Create("test/model", 512, 0.5f, 8192).Value!,
            new AgentHostOptions(
                FixedWorkspaceContext.ForPath("app"), new UnrootedPathResolver()), db.Database)
        .BuildServiceProvider();

    Assert.NotNull(services.GetRequiredService<ILanguageServerCatalog>());
    Assert.NotNull(services.GetRequiredService<ILanguageServerInstaller>());
    Assert.NotNull(services.GetRequiredService<IWorkspaceLanguageDetector>());
    Assert.Equal(10, services.GetRequiredService<ILanguageServerCatalog>().All.Count);
  }
}
