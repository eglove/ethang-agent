using eThangAgent.AgentDomain;
using eThangAgent.CapabilityDomain;
using eThangAgent.ModelDomain;
using eThangAgent.SharedKernel;
using eThangAgent.SkillDomain;
using eThangAgent.Storage.ACL;
using eThangAgent.TestSupport;
using eThangAgent.ToolDomain;
using Microsoft.Extensions.DependencyInjection;

namespace eThangAgent.Composition.Tests;

/// <summary>The announced tools, proved over a COMPOSED SESSION rather than over a provider in
///     isolation (plan #209, step 6).
///     <para>THE TWO GUARANTEES THIS CLASS EXISTS FOR: a user who ticks nothing - or whose ticked
///     names no longer resolve - gets a prompt byte-identical to the one they got before this
///     capability existed, and every name the prompt announces is one the session's OWN exec
///     surface can actually start. The second is why the run goes through the session's
///     <see cref="IExecEngine"/>: a prompt that names a tool the model then fails to run is the
///     exact failure this work exists to end, and only the composed container proves the
///     announcement and the spawn agree.</para>
///     <para>The PATH is prepended rather than replaced, with a name unique to each test run, so
///     the shim resolves without a directory this machine happens to hold and no concurrent test
///     can collide with it.</para></summary>
[Trait("Category", "Process")]
public sealed class AnnouncedToolsSessionTests
{
  private static readonly Uri BaseUrl = new("https://openrouter.test");

  /// <summary>The lead-in of the announcement. Asserted as an ABSENCE in the unconfigured cases:
  ///     the criterion is that nothing was added, not that a particular sentence is missing.</summary>
  private const string AnnouncementLeadIn = "Command-line tools on this machine";

  private static AgentSettings Settings() => new(
      new OpenRouterSettings("sk-or-test", BaseUrl),
      new AgentRunOptions(null, 2));

  /// <summary>The prompt the session's own container composes - the same bytes the model
  ///     receives as the leading part of every request.</summary>
  private static string Prompt(AgentSession session) =>
      session.Services.GetRequiredService<ISystemPromptProvider>().Build();

  [Fact]
  public async Task ASessionThatTicksNothing_ComposesThePromptItComposedBeforeTheCapabilityExisted()
  {
    // THE UNCONFIGURED CASE IS THE ONE THAT MUST BE BYTE-IDENTICAL: a user who never opens the
    // Tools page must see no change at all.
    string dbPath = NewDatabasePath();
    DirectoryInfo workspace = TestTempRoot.CreateDirectory("ethang-announced-none");
    try
    {
      AgentSessionFactory factory = new(Settings(), new AppDatabase(dbPath));
      Result<AgentSession> created = await factory.CreateAsync(workspace.FullName, Providers.OpenRouter,
          conversationSeed: null, chainContract: null, TestContext.Current.CancellationToken);
      Assert.True(created.IsSuccess, created.Error?.Message);
      try
      {
        string prompt = Prompt(created.Value);

        Assert.Equal(CompositeWithoutTheAnnouncement(created.Value.Services, workspace.FullName), prompt,
            StringComparer.Ordinal);
        Assert.DoesNotContain(AnnouncementLeadIn, prompt, StringComparison.Ordinal);
      }
      finally
      {
        await created.Value.Services.DisposeAsync();
      }
    }
    finally
    {
      DeleteDirectory(workspace);
      TryDelete(dbPath);
    }
  }

  [Fact]
  public async Task ATickedToolThatResolves_IsAnnouncedAndRunsThroughTheSessionsExecSurface()
  {
    string name = UniqueName();
    DirectoryInfo toolDirectory = TestTempRoot.CreateDirectory("ethang-announced-tool");
    _ = WriteScript(toolDirectory.FullName, name, "echo announced-ok");
    string dbPath = NewDatabasePath();
    DirectoryInfo workspace = TestTempRoot.CreateDirectory("ethang-announced-ticked");
    string? previousPath = Environment.GetEnvironmentVariable("PATH");
    Environment.SetEnvironmentVariable("PATH", toolDirectory.FullName + Path.PathSeparator + previousPath);
    try
    {
      AppDatabase database = new(dbPath);
      await StoreAsync(database, PathToolPreferences.Serialize([name]));
      AgentSessionFactory factory = new(Settings(), database);
      Result<AgentSession> created = await factory.CreateAsync(workspace.FullName, Providers.OpenRouter,
          conversationSeed: null, chainContract: null, TestContext.Current.CancellationToken);
      Assert.True(created.IsSuccess, created.Error?.Message);
      try
      {
        string prompt = Prompt(created.Value);
        Assert.Contains(name, prompt, StringComparison.Ordinal);

        // THE ANNOUNCEMENT MUST BE TRUE: the name the prompt just told the model about is run
        // through this session's own exec surface, and its exit code is the answer.
        ExecRunResult run = await created.Value.Services.GetRequiredService<IExecEngine>().ExecuteAsync(
            new ExecProgram($"var r = Shell(\"{name}\"); return r.ExitCode + \"|\" + r.Stdout.Trim();"),
            TestContext.Current.CancellationToken);

        Assert.Equal("0|announced-ok", run.Output.Trim());
      }
      finally
      {
        await created.Value.Services.DisposeAsync();
      }
    }
    finally
    {
      Environment.SetEnvironmentVariable("PATH", previousPath);
      DeleteDirectory(toolDirectory);
      DeleteDirectory(workspace);
      TryDelete(dbPath);
    }
  }

  [Fact]
  public async Task ATickedToolThatDoesNotResolve_IsAbsentFromThePromptAndTheSessionStillOpens()
  {
    // SILENT OMISSION, stated as a byte comparison: a selection that resolves nothing is
    // indistinguishable from no selection at all, and the session opens either way.
    string name = UniqueName();
    string dbPath = NewDatabasePath();
    DirectoryInfo workspace = TestTempRoot.CreateDirectory("ethang-announced-missing");
    try
    {
      AppDatabase database = new(dbPath);
      AgentSessionFactory factory = new(Settings(), database);

      Result<AgentSession> plain = await factory.CreateAsync(workspace.FullName, Providers.OpenRouter,
          conversationSeed: null, chainContract: null, TestContext.Current.CancellationToken);
      Assert.True(plain.IsSuccess, plain.Error?.Message);
      string before = Prompt(plain.Value);
      await plain.Value.Services.DisposeAsync();

      await StoreAsync(database, PathToolPreferences.Serialize([name]));

      Result<AgentSession> ticked = await factory.CreateAsync(workspace.FullName, Providers.OpenRouter,
          conversationSeed: null, chainContract: null, TestContext.Current.CancellationToken);
      Assert.True(ticked.IsSuccess, ticked.Error?.Message);
      try
      {
        string after = Prompt(ticked.Value);

        Assert.DoesNotContain(name, after, StringComparison.Ordinal);
        Assert.DoesNotContain(AnnouncementLeadIn, after, StringComparison.Ordinal);
        Assert.Equal(before, after, StringComparer.Ordinal);
      }
      finally
      {
        await ticked.Value.Services.DisposeAsync();
      }
    }
    finally
    {
      DeleteDirectory(workspace);
      TryDelete(dbPath);
    }
  }

  // ---- Fixtures ----

  /// <summary>The composite the same container builds with the announcement left out - the
  ///     prompt a session composed before this capability existed. Assembled from the session's
  ///     OWN resolved services so the comparison is against this build's text, not a copy of it.
  ///     <para>The frontend's session-file provider is included because the factory appends it to
  ///     every container; with nothing configured it renders empty and the composite drops it.</para></summary>
  private static string CompositeWithoutTheAnnouncement(IServiceProvider services, string workspaceRoot) =>
      new CompositeSystemPromptProvider(
      [
          new SkillsBootstrapPromptProvider(services.GetRequiredService<ISkillCatalog>(),
              services.GetRequiredService<ICommitStyleProvider>()),
          new SkillsListingPromptProvider(services.GetRequiredService<ISkillCatalog>(),
              services.GetRequiredService<ResolvedSkillDirectories>().List),
          new StaticPromptProvider(
              "You are eThang Agent, an AI coding agent for Windows. Work in the current " +
              "workspace, prefer the provided tools over guessing, and keep responses tight."),
          new StakeholderVoicePromptProvider(),
          new ExecGuidePromptProvider(
              new Lazy<ICapabilityRegistry>(services.GetRequiredService<ICapabilityRegistry>)),
          new ContextHygienePromptProvider(),
          new ComputedValuesPromptProvider(),
          new RetrospectivePromptProvider(),
          new TrustedDirectoriesPromptProvider(services.GetRequiredService<ResolvedTrustedDirectories>().List),
          new SessionFilesPromptProvider(workspaceRoot, null, null),
      ]).Build();

  /// <summary>A name no other test in this run can collide with, so prepending a directory to the
  ///     process PATH cannot shadow anything a concurrent test resolves.</summary>
  private static string UniqueName() => "ethang-announced-" + Guid.NewGuid().ToString("N")[..10];

  /// <summary>A temp directory holding one <c>.cmd</c> script: the shim shape this machine's
  ///     node tools publish, and the one that could not be started before the resolution fix.</summary>
  private static string WriteScript(string directory, string name, params string[] lines)
  {
    string path = Path.Combine(directory, name + ".cmd");
    File.WriteAllLines(path, ["@echo off", .. lines]);
    return path;
  }

  private static async Task StoreAsync(AppDatabase database, string value)
  {
    SqliteAppPreferenceStore store = new(database);
    bool stored = await store.SetAsync(PathToolPreferences.Key, value,
        TestContext.Current.CancellationToken).ConfigureAwait(false);
    Assert.True(stored, "the ticked-tool preference did not land in the test database");
  }

  private static string NewDatabasePath() =>
      Path.Combine(TestTempRoot.Root, $"ethang-announced-{Guid.NewGuid():N}.db");

  private static void DeleteDirectory(DirectoryInfo directory)
  {
    // Named decision (CA1031): best-effort cleanup of a temp tree; a delete fault must not mask
    // the assertion that ran before it.
#pragma warning disable CA1031 // Do not catch general exception types
    try
    {
      directory.Delete(recursive: true);
    }
    catch (Exception)
#pragma warning restore CA1031 // Do not catch general exception types
    {
      // Swallowed deliberately: see the summary above.
    }
  }

  /// <summary>Best-effort temp-database cleanup, by the same rule as the directory above.</summary>
  private static void TryDelete(string path)
  {
#pragma warning disable CA1031 // Do not catch general exception types
    try
    {
      File.Delete(path);
    }
    catch (Exception)
#pragma warning restore CA1031 // Do not catch general exception types
    {
      // Swallowed deliberately: see the summary above.
    }
  }
}
