using eThangAgent.AgentDomain;
using eThangAgent.ModelDomain;
using eThangAgent.Storage.ACL;
using eThangAgent.TestSupport;
using eThangAgent.ToolDomain;
using eThangAgent.ToolDomain.Lsp;
using Microsoft.Extensions.DependencyInjection;
using LspAdvisoryDiagnosticsAccess = eThangAgent.Lsp.ACL.LspAdvisoryDiagnosticsAccess;
using LspDaemonExePathOverride = eThangAgent.Lsp.ACL.LspDaemonExePathOverride;
using LspDaemonHost = eThangAgent.Lsp.ACL.LspDaemonHost;
using LspDaemonSymbolAccess = eThangAgent.Lsp.ACL.LspDaemonSymbolAccess;

namespace eThangAgent.Composition.Tests;

/// <summary>The language-server seams as the composed container wires them: the catalog, the
///     installer, and the workspace detector resolve in a container with nothing installed,
///     the symbol tool and the advisory diagnostics source ride the one daemon host that
///     container owns, and the daemon starts only for a workspace that holds a language this
///     app serves - not for one that merely holds C#-shaped files.
///     <para>The composed container is a real one over a throwaway database, and the daemon
///     start decision is exercised against a daemon executable that does not exist, which is
///     what earns the Process trait.</para></summary>
[Trait("Category", "Process")]
public class LanguageServerCompositionTests
{
  private static AgentSettings Settings() => new(
      new OpenRouterSettings("sk-or-test", new Uri("https://openrouter.test")),
      new AgentRunOptions(null, 2));

  /// <summary>A composed container rooted at <paramref name="root"/>, with the daemon
  ///     executable pointed at a path that does not exist so a start attempt cannot reach a
  ///     real server.</summary>
  private static ServiceProvider Compose(TestAppDatabase db, string root)
  {
    ServiceProvider services = new ServiceCollection()
        .AddEThangAgentCore(Settings(), Providers.OpenRouter,
            ModelConfig.Create("test/model", 512, 0.5f, 8192).Value!,
            new AgentHostOptions(FixedWorkspaceContext.ForPath(root), new UnrootedPathResolver()),
            db.Database)
        .BuildServiceProvider();
    services.GetRequiredService<LspDaemonExePathOverride>().Path =
        Path.Combine(root, "no-such-daemon", "eThangAgent.Lsp.Host.exe");
    return services;
  }

  private static DirectoryInfo Workspace(string prefix, params string[] files)
  {
    DirectoryInfo directory = TestTempRoot.CreateDirectory(prefix);
    foreach (string file in files)
    {
      string path = Path.Combine(directory.FullName, file);
      _ = Directory.CreateDirectory(Path.GetDirectoryName(path)!);
      File.WriteAllText(path, string.Empty);
    }

    return directory;
  }

  [Fact]
  public async Task TheSeamsAndTheDaemonAccessors_ResolveWithNothingInstalled()
  {
    using TestAppDatabase db = TestAppDatabase.Create();
    DirectoryInfo root = Workspace("ethang-lsp-compose", "README.md");
    try
    {
      using ServiceProvider services = Compose(db, root.FullName);

      ILanguageServerCatalog catalog = services.GetRequiredService<ILanguageServerCatalog>();
      ILanguageServerInstaller installer = services.GetRequiredService<ILanguageServerInstaller>();
      IWorkspaceLanguageDetector detector = services.GetRequiredService<IWorkspaceLanguageDetector>();

      Assert.Equal(10, catalog.All.Count);
      Assert.Empty((await installer.ReadAsync(TestContext.Current.CancellationToken)
          .ConfigureAwait(true)).LanguageIds);
      Assert.Empty(detector.Detect(root.FullName));
      Assert.NotNull(services.GetRequiredService<LspDaemonHost>());
      Assert.NotNull(services.GetRequiredService<LspDaemonSymbolAccess>());
      Assert.NotNull(services.GetRequiredService<LspAdvisoryDiagnosticsAccess>());
    }
    finally
    {
      root.Delete(true);
    }
  }

  [Fact]
  public async Task TheSymbolToolAndTheAdvisorySource_RideTheOneDaemonHost()
  {
    using TestAppDatabase db = TestAppDatabase.Create();
    DirectoryInfo root = Workspace("ethang-lsp-one-host", "Cargo.toml");
    try
    {
      using ServiceProvider services = Compose(db, root.FullName);

      LspDaemonHost host = services.GetRequiredService<LspDaemonHost>();
      LspDaemonSymbolAccess symbol = services.GetRequiredService<LspDaemonSymbolAccess>();
      LspAdvisoryDiagnosticsAccess advisory = services.GetRequiredService<LspAdvisoryDiagnosticsAccess>();

      // One host per container is what makes the claim gate, the symbol tool, and the
      // advisory source share a connection: each of them resolves this same instance.
      Assert.Same(host, services.GetRequiredService<LspDaemonHost>());
      Assert.Same(host, symbol.Host);
      Assert.Same(host, advisory.Host);
      Assert.Same(symbol, services.GetRequiredService<LspDaemonSymbolAccess>());

      // The accessor is live: a query for a file that is not there answers without
      // reaching the daemon, so the wiring is exercised without a server installed.
      LspSymbolOutcome outcome = await symbol
          .ExecuteAsync(new LspSymbolCommand.Hover(Path.Combine(root.FullName, "a.rs"), "main", 1),
              TestContext.Current.CancellationToken)
          .ConfigureAwait(true);
      _ = Assert.IsType<LspSymbolOutcome.Unsupported>(outcome);
    }
    finally
    {
      root.Delete(true);
    }
  }

  [Fact]
  public void StartLspDaemon_StartsTheDaemonForAWorkspaceWhoseOnlyLanguageIsNotCSharp()
  {
    using TestAppDatabase db = TestAppDatabase.Create();
    DirectoryInfo root = Workspace("ethang-lsp-non-csharp", "Cargo.toml");
    try
    {
      using ServiceProvider services = Compose(db, root.FullName);
      LspDaemonHost daemon = services.GetRequiredService<LspDaemonHost>();

      Assert.Equal(["rust"], [.. daemon.Languages.Select(detected => detected.LanguageId)]);
      Assert.False(daemon.IsStarted);

      AgentComposition.StartLspDaemon(services);

      Assert.True(daemon.IsStarted,
          "a workspace whose only language is Rust must start the daemon; a C#-only start "
          + "decision leaves every other language without a server.");
    }
    finally
    {
      root.Delete(true);
    }
  }

  [Fact]
  public void StartLspDaemon_StartsNothingForAWorkspaceHoldingNoLanguage()
  {
    using TestAppDatabase db = TestAppDatabase.Create();
    DirectoryInfo root = Workspace("ethang-lsp-no-language", "README.md", "notes/todo.txt");
    try
    {
      using ServiceProvider services = Compose(db, root.FullName);
      LspDaemonHost daemon = services.GetRequiredService<LspDaemonHost>();

      Assert.Empty(daemon.Languages);

      AgentComposition.StartLspDaemon(services);

      Assert.False(daemon.IsStarted,
          "a workspace holding no language this app serves must not start a daemon.");
    }
    finally
    {
      root.Delete(true);
    }
  }

  [Fact]
  public async Task AnInstallRecordedInTheStoresPreference_IsVisibleToTheComposedInstaller()
  {
    using TestAppDatabase db = TestAppDatabase.Create();
    DirectoryInfo root = Workspace("ethang-lsp-installed", "README.md");
    try
    {
      using ServiceProvider services = Compose(db, root.FullName);
      ILanguageServerInstaller installer = services.GetRequiredService<ILanguageServerInstaller>();
      Assert.Empty((await installer.ReadAsync(TestContext.Current.CancellationToken)
          .ConfigureAwait(true)).LanguageIds);

      // The same preference row a page install writes, written through the container's own
      // store: the composed installer reads it back with no container rebuild.
      _ = await services.GetRequiredService<IAppPreferenceStore>()
          .SetAsync(LanguageServerPreferences.InstalledKey,
              /*lang=json,strict*/ "[{\"languageId\":\"lua\",\"version\":\"3.19.2\"}]",
              TestContext.Current.CancellationToken)
          .ConfigureAwait(true);

      InstalledLanguageServers installed = await installer
          .ReadAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);

      Assert.Equal(["lua"], installed.LanguageIds);
      Assert.Equal("3.19.2", installed.Version("lua"));
    }
    finally
    {
      root.Delete(true);
    }
  }
}
