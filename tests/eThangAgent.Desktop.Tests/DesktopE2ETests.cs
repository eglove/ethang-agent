using System.Text.Json;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using eThangAgent.Agent.Application;
using eThangAgent.Agent.Application.Sessions;
using eThangAgent.AgentDomain;
using eThangAgent.Composition;
using eThangAgent.Desktop.ViewModels;
using eThangAgent.Desktop.Views;
using eThangAgent.ModelDomain;
using eThangAgent.SharedKernel;
using eThangAgent.Storage.ACL;
using eThangAgent.TestSupport;
using eThangAgent.ToolDomain;

namespace eThangAgent.Desktop.Tests;

/// <summary>
/// Headless end-to-end coverage ported from the retired piped-CLI suite: the REAL
/// composition answers through the mock provider and the view-model renders it.
/// Provider-contract scenarios: configured model selection, skills bootstrap,
/// exposed tool surface, exec guide injection.
/// </summary>
[Collection("Desktop E2E")]
[Trait("Category", "Process")]
public class DesktopE2ETests
{
  private static string RawCompletion(string content) => E2E.RawCompletion(content);

  [Fact]
  public async Task Turn_SendsConfiguredDefaultModel_ToProvider()
  {
#pragma warning disable CA2007 // 'await using' cannot carry ConfigureAwait.
    await using E2E.HostHarness host = new();
#pragma warning restore CA2007
    host.ClaimHostsForCurrentTest();
    _ = await host.StartAsync();

    await host.Vm.RunTurnAsync("hi");

    Assert.NotNull(host.Mock.LastChatRequestBody);
    Assert.Contains(E2E.SessionModel, host.Mock.LastChatRequestBody, StringComparison.Ordinal);
  }

  [Fact]
  public async Task Turn_InjectsSkillsBootstrap_OncePerSession()
  {
#pragma warning disable CA2007 // 'await using' cannot carry ConfigureAwait.
    await using E2E.HostHarness host = new();
#pragma warning restore CA2007
    host.ClaimHostsForCurrentTest();
    _ = await host.StartAsync();

    await host.Vm.RunTurnAsync("hi");

    string? body = host.Mock.LastChatRequestBody;
    Assert.NotNull(body);
    // The wire body JSON-escapes angle brackets (\u003C/\u003E), so assertions on
    // injected content run against the decoded instructions string, not the raw body.
    using JsonDocument doc = JsonDocument.Parse(body);
    string? system = doc.RootElement.GetProperty("instructions").GetString();
    Assert.NotNull(system);
    Assert.DoesNotContain("EXTREMELY_IMPORTANT", system, StringComparison.Ordinal);
    Assert.Contains("name: using-skills", system, StringComparison.Ordinal);
    Assert.Contains("ALREADY ACTIVE", system, StringComparison.Ordinal);
    Assert.Contains("skill_view", system, StringComparison.Ordinal);
    // The frontmatter header is the once-only structural marker for the
    // bootstrap contract: the listing provider never renders frontmatter, and
    // the already-active notice sentence also occurs inside the
    // ethang-tools-mapping body, so it cannot count as once-only.
    Assert.Equal(1, System.Text.RegularExpressions.Regex.Count(system,
        System.Text.RegularExpressions.Regex.Escape("---\nname: using-skills")));
  }

  [Fact]
  public async Task ModelToolsContainOnlyExec()
  {
#pragma warning disable CA2007 // 'await using' cannot carry ConfigureAwait.
    await using E2E.HostHarness host = new();
#pragma warning restore CA2007
    host.ClaimHostsForCurrentTest();
    _ = await host.StartAsync();

    await host.Vm.RunTurnAsync("hi");

    Assert.NotNull(host.Mock.LastChatRequestBody);
    Assert.Contains("\"name\":\"exec\"", host.Mock.LastChatRequestBody, StringComparison.Ordinal);
    Assert.DoesNotContain("\"name\":\"read\"", host.Mock.LastChatRequestBody, StringComparison.Ordinal);
  }

  [Fact]
  public async Task SendsExecGuide_InSystemPrompt()
  {
#pragma warning disable CA2007 // 'await using' cannot carry ConfigureAwait.
    await using E2E.HostHarness host = new();
#pragma warning restore CA2007
    host.ClaimHostsForCurrentTest();
    _ = await host.StartAsync();

    await host.Vm.RunTurnAsync("hi");

    Assert.NotNull(host.Mock.LastChatRequestBody);
    Assert.Contains("\"instructions\"", host.Mock.LastChatRequestBody, StringComparison.Ordinal);
    Assert.Contains("writing C# programs", host.Mock.LastChatRequestBody, StringComparison.Ordinal);
    // Stable fragments only: parameter lists change with legitimate descriptor
    // evolution; action names and summaries are the durable contract.
    Assert.Contains("get(", host.Mock.LastChatRequestBody, StringComparison.Ordinal);
    Assert.Contains("Read a durable state value", host.Mock.LastChatRequestBody, StringComparison.Ordinal);
    Assert.Contains(
        "verify(ids: String[]): Run attached evidence fail-closed and certify.",
        host.Mock.LastChatRequestBody, StringComparison.Ordinal);
    Assert.Contains(
        "read(timeoutSeconds: WholeNumber, path: Text, startLine: WholeNumber, endLine: WholeNumber, force: Flag): Read lines from a text file.",
        host.Mock.LastChatRequestBody, StringComparison.Ordinal);
    Assert.Contains(
        "db_query(timeoutSeconds: WholeNumber, sql: Text, maxRows: WholeNumber): Run one read-only SQL query",
        host.Mock.LastChatRequestBody, StringComparison.Ordinal);
    Assert.Contains(
        "sqlite_query(timeoutSeconds: WholeNumber, path: Text, sql: Text, maxRows: WholeNumber): Run one read-only SQL query against any SQLite file in the workspace.",
        host.Mock.LastChatRequestBody, StringComparison.Ordinal);
    Assert.Contains(
        "db_schema(timeoutSeconds: WholeNumber, includeCounts: Flag): List the tables, columns, and indexes",
        host.Mock.LastChatRequestBody, StringComparison.Ordinal);
  }

  [Fact]
  public async Task ExecutesExecTool_EndToEnd()
  {
#pragma warning disable CA2007 // 'await using' cannot carry ConfigureAwait.
    await using E2E.HostHarness host = new();
#pragma warning restore CA2007
    host.ClaimHostsForCurrentTest();
    _ = await host.StartAsync();

    string tempFile = Path.Combine(TestTempRoot.Root, $"ethang-exec-{Guid.NewGuid():N}.txt");
    await File.WriteAllLinesAsync(tempFile, ["alpha line", "beta line"], TestContext.Current.CancellationToken);

    string pathArg = tempFile.Replace("\\", "\\\\", StringComparison.Ordinal);
    string program = $"return Tools.read(new {{ timeoutSeconds = 120, path = \"{pathArg}\", startLine = 1, endLine = 2 }});";
    _ = host.Mock.Returns(E2E.ExecToolCall("call_1", E2E.ExecProgram(program)));
    _ = host.Mock.Returns(RawCompletion("exec completed"));

    await host.Vm.RunTurnAsync("run a program");

    string assistant = string.Join("", host.Vm.Transcript.Entries
        .OfType<AssistantTextEntry>().Select(a => a.Text));
    Assert.Contains("exec completed", assistant, StringComparison.OrdinalIgnoreCase);
    Assert.Equal(2, host.Mock.RequestBodies.Count);
    Assert.Contains("\"type\":\"function_call_output\"", host.Mock.RequestBodies[1], StringComparison.Ordinal);
    Assert.Contains("alpha line", host.Mock.RequestBodies[1], StringComparison.Ordinal);

    // Named decision (CA1031): temp-file cleanup is best effort.
#pragma warning disable CA1031, S108 // Do not catch general exception types
    try
    {
      File.Delete(tempFile);
    }
    catch { }
#pragma warning restore CA1031, S108
  }

  [Fact]
  public async Task Exec_ParseErrorFeedsBack_AndCorrectedProgramSucceeds()
  {
#pragma warning disable CA2007 // 'await using' cannot carry ConfigureAwait.
    await using E2E.HostHarness host = new();
#pragma warning restore CA2007
    host.ClaimHostsForCurrentTest();
    _ = await host.StartAsync();

    string broken = JsonSerializer.Serialize(new { program = "if (x {" });
    string corrected = JsonSerializer.Serialize(
        new { program = "Write-Output 'corrected output'" });
    _ = host.Mock.Returns(E2E.ExecToolCall("call_1", broken));
    _ = host.Mock.Returns(E2E.ExecToolCall("call_2", corrected));
    _ = host.Mock.Returns(RawCompletion("done"));

    await host.Vm.RunTurnAsync("try exec");

    string assistant = string.Join("", host.Vm.Transcript.Entries
        .OfType<AssistantTextEntry>().Select(a => a.Text));
    Assert.Contains("done", assistant, StringComparison.OrdinalIgnoreCase);

    Assert.Equal(3, host.Mock.RequestBodies.Count);
    Assert.Contains("ExecParseError", host.Mock.RequestBodies[1], StringComparison.Ordinal);
    Assert.Contains("ExecParseError", host.Mock.RequestBodies[2], StringComparison.Ordinal);
    Assert.Contains("corrected output", host.Mock.RequestBodies[2], StringComparison.Ordinal);
  }
}

/// <summary>The whole startup cycle, end to end, against the local mock provider: the launcher
///     is what a launch shows, a chosen workspace becomes one main window bound to that
///     directory, its agents are tabs in that window, and closing the workspace returns to the
///     launcher with the directory now in the recent list.
///     <para>WHAT MAKES THIS THE PRODUCTION SEQUENCE: the windows, the instance gate and the
///     recent-workspace record are driven through <see cref="WorkspaceHost"/>, which is the
///     object <c>App</c> hands its own lifetime to. Headless Avalonia supplies no
///     classic-desktop lifetime, so the application object's startup never runs here; the host
///     is the seam that makes the steps reachable without reimplementing them.</para>
///     <para>NO REAL NETWORK: every provider call lands on the mock server this test starts on
///     loopback.</para></summary>
[Collection("Desktop E2E")]
[Trait("Category", "Process")]
public class WorkspaceLauncherE2ETests
{
  /// <summary>Launcher, workspace, two agents, close, launcher again - the assertion sequence the
  ///     requirement states, in order.</summary>
  [AvaloniaFact]
  public async Task LauncherToWorkspaceToLauncher_CoversTheWholeStartupCycle()
  {
    CancellationToken ct = TestContext.Current.CancellationToken;
    using TestAppDatabase storage = TestAppDatabase.Create();
    using MockOpenRouterServer provider = new();
    provider.Start();
    _ = provider.ReturnsCatalog(CatalogJson());
    _ = provider.Returns(CompletionJson());
    DirectoryInfo workspace = TestTempRoot.CreateDirectory("ethang-launcher-e2e");
    string root = WorkspaceIdentity.Normalize(workspace.FullName);

    using ClassicDesktopStyleApplicationLifetime lifetime = new();
    DesktopHost.DeferShutdownDuringStartup(lifetime);
    using WorkspaceHost host = new(lifetime, Bootstrap(storage, provider, workspace.FullName));

    // 1. THE LAUNCHER IS THE WHOLE OF STARTUP: one window, no workspace, nothing remembered yet.
    host.ShowLauncher();
    Dispatcher.UIThread.RunJobs();
    LauncherWindow launcher = host.Launcher
        ?? throw new InvalidOperationException("the launcher must be showing after startup");
    Assert.True(launcher.IsVisible);
    Assert.Same(launcher, lifetime.MainWindow);
    Assert.Null(host.Session);
    LauncherViewModel launcherVm = Assert.IsType<LauncherViewModel>(launcher.DataContext);
    Assert.Empty(launcherVm.Recents);

    // 2. CHOOSING THE DIRECTORY HANDS OFF TO ONE MAIN WINDOW BOUND TO IT.
    bool opened = await host.OpenWorkspaceAsync(new WorkspaceHandoff(workspace.FullName))
        .WaitAsync(TestCeiling.Value, ct).ConfigureAwait(true);
    Assert.True(opened, "the chosen workspace must open");
    Dispatcher.UIThread.RunJobs();
    DesktopSession session = host.Session
        ?? throw new InvalidOperationException("the handoff must produce a session");
    Assert.Equal(root, session.WorkspaceRoot);
    Assert.True(session.Window.IsVisible);
    Assert.Equal("eThang Agent — " + workspace.Name, session.Window.Title);
    Assert.False(launcher.IsVisible, "the launcher closes on the handoff");
    Assert.Same(session.Window, lifetime.MainWindow);

    // 3. TWO AGENTS ARE TWO CARDS IN THAT ONE WINDOW'S GRID: one window is one workspace and
    // one grid, so a second agent joins the grid rather than opening a second one.
    MainViewModel vm = Assert.IsType<MainViewModel>(session.Window.DataContext);
    Assert.Empty(vm.Tabs);
    for (int i = 0; i < 2; i++)
    {
      Result<AgentCellViewModel> added = await vm.AddAgentInWorkspaceAsync()
          .WaitAsync(TestCeiling.Value, ct).ConfigureAwait(true);
      Assert.True(added.IsSuccess, added.Error?.Message);
    }

    AgentTabViewModel grid = Assert.Single(vm.Tabs);
    Assert.Equal(2, grid.Cells.Count);
    Assert.Equal(root, grid.WorkspaceRoot);

    // 4. CLOSING THE WORKSPACE RETURNS TO THE LAUNCHER, and the directory is now remembered.
    bool closed = await host.CloseWorkspaceAsync().WaitAsync(TestCeiling.Value, ct).ConfigureAwait(true);
    Assert.True(closed, "a workspace was open, so closing it must do something");
    Dispatcher.UIThread.RunJobs();
    Assert.Null(host.Session);
    Assert.Empty(vm.Tabs);
    Assert.False(session.Window.IsVisible, "closing the workspace closes its window");

    LauncherWindow returned = host.Launcher
        ?? throw new InvalidOperationException("closing the workspace must return to the launcher");
    Assert.True(returned.IsVisible);
    Assert.Same(returned, lifetime.MainWindow);
    LauncherViewModel returnedVm = Assert.IsType<LauncherViewModel>(returned.DataContext);
    await PumpUntilAsync(() => returnedVm.Recents.Count > 0, ct).ConfigureAwait(true);
    RecentWorkspaceRow row = Assert.Single(returnedVm.Recents);
    Assert.Equal(root, row.Root);
    Assert.True(row.Exists);
  }

  /// <summary>A second launch for a directory this process already has open is handed off: the
  ///     running window is raised, the new launch opens no window of its own, and the workspace
  ///     keeps exactly one window writing its history.
  ///     <para>THE EXIT CODE IS NOT OBSERVED HERE. This test drives two hosts inside one process,
  ///     so the launch-level decision - signal the owner, then exit with the handoff code before
  ///     any window exists - is <see cref="LaunchHandoffTests"/>'s subject, where the decision is
  ///     observed without opening a window.</para></summary>
  [AvaloniaFact]
  public async Task SecondLaunch_ForAnOpenWorkspace_IsHandedOff_NotASecondWindow()
  {
    CancellationToken ct = TestContext.Current.CancellationToken;
    using TestAppDatabase storage = TestAppDatabase.Create();
    using MockOpenRouterServer provider = new();
    provider.Start();
    _ = provider.ReturnsCatalog(CatalogJson());
    _ = provider.Returns(CompletionJson());
    DirectoryInfo workspace = TestTempRoot.CreateDirectory("ethang-handoff-e2e");
    string root = WorkspaceIdentity.Normalize(workspace.FullName);

    using ClassicDesktopStyleApplicationLifetime firstLifetime = new();
    DesktopHost.DeferShutdownDuringStartup(firstLifetime);
    using WorkspaceHost first = new(firstLifetime, Bootstrap(storage, provider, workspace.FullName));
    first.ShowLauncher();
    Dispatcher.UIThread.RunJobs();
    Assert.True(await first.OpenWorkspaceAsync(new WorkspaceHandoff(workspace.FullName))
        .WaitAsync(TestCeiling.Value, ct).ConfigureAwait(true));
    Dispatcher.UIThread.RunJobs();
    DesktopSession session = first.Session
        ?? throw new InvalidOperationException("the first launch must open the workspace");

    // The name is owned: this is the fact that makes the next launch a handoff rather than a
    // second window.
    Assert.True(WorkspaceInstanceHandoff.TrySignalExisting(root),
        "the open workspace must own its instance name");

    // 2. THE SECOND LAUNCH: its own lifetime, its own host, the same directory.
    using ClassicDesktopStyleApplicationLifetime secondLifetime = new();
    DesktopHost.DeferShutdownDuringStartup(secondLifetime);
    using WorkspaceHost second = new(secondLifetime, Bootstrap(storage, provider, workspace.FullName));
    second.ShowLauncher();
    Dispatcher.UIThread.RunJobs();
    LauncherWindow secondLauncher = second.Launcher
        ?? throw new InvalidOperationException("the second launch starts on its own launcher");

    bool openedAgain = await second.OpenWorkspaceAsync(new WorkspaceHandoff(workspace.FullName))
        .WaitAsync(TestCeiling.Value, ct).ConfigureAwait(true);
    Dispatcher.UIThread.RunJobs();

    Assert.False(openedAgain, "a workspace another process owns is signalled, not opened");
    Assert.Null(second.Session);
    Assert.Same(secondLauncher, second.Launcher);
    Assert.Same(secondLauncher, secondLifetime.MainWindow);

    // The FIRST launch is untouched: one window, still bound to the directory, still visible.
    Assert.Same(session, first.Session);
    Assert.True(session.Window.IsVisible, "the owner's window stays open");
    Assert.Same(session.Window, firstLifetime.MainWindow);
    Assert.Null(first.Launcher);

    // Handing the workspace back releases the name and the window, so the next launch opens.
    Assert.True(await first.CloseWorkspaceAsync().WaitAsync(TestCeiling.Value, ct).ConfigureAwait(true));
    Dispatcher.UIThread.RunJobs();
    Assert.False(WorkspaceInstanceHandoff.TrySignalExisting(root),
        "a closed workspace no longer owns its instance name");
  }

  /// <summary>The catalog the mock serves: the one model a session pins and its window facts.
  ///     A pinned model with no catalog row fails the turn loudly, so the row is what makes the
  ///     composition resolve the same way production does.</summary>
  private static string CatalogJson() => /*lang=json,strict*/
      "{ \"data\": [ { \"id\": \"openrouter/auto\", \"pricing\": { \"prompt\": \"-1\", \"completion\": \"-1\" }, \"context_length\": 2000000, \"supported_parameters\": [\"tools\"], \"architecture\": { \"modality\": \"text+image->text\", \"input_modalities\": [\"text\", \"image\"] }, \"top_provider\": { \"context_length\": null, \"max_completion_tokens\": null } } ] }";

  /// <summary>A scripted assistant reply, in the Responses-API body shape.</summary>
  private static string CompletionJson() => /*lang=json,strict*/
      "{ \"output\": [ { \"type\": \"message\", \"role\": \"assistant\", \"content\": [ { \"type\": \"output_text\", \"text\": \"ready\" } ] } ], \"status\": \"completed\" }";

  /// <summary>Runs the dispatcher until <paramref name="condition"/> holds, bounded by the test
  ///     ceiling so a list that never loads fails instead of hanging. The recent list loads off
  ///     the UI thread, so its arrival is an event to wait for rather than a fact to assume.
  ///     </summary>
  /// <param name="condition">The outcome to wait for.</param>
  /// <param name="ct">Cancellation for the run.</param>
  private static async Task PumpUntilAsync(Func<bool> condition, CancellationToken ct)
  {
    DateTimeOffset deadline = DateTimeOffset.UtcNow + TestCeiling.Value;
    while (!condition() && DateTimeOffset.UtcNow < deadline)
    {
      await Task.Delay(TestTempRoot.DeleteRetryInterval, ct).ConfigureAwait(true);
      Dispatcher.UIThread.RunJobs();
    }

    Dispatcher.UIThread.RunJobs();
    Assert.True(condition(), "the launcher's recent list never loaded.");
  }

  /// <summary>A bootstrap over throwaway storage and the mock provider: the real session factory,
  ///     catalog, preference store and recent-workspace store, with the retention sweep and the
  ///     sign-in flow stubbed so the test observes the startup cycle and nothing else.
  ///     </summary>
  /// <param name="storage">The throwaway database.</param>
  /// <param name="provider">The mock provider serving every call.</param>
  /// <param name="workspaceRoot">The directory the sessions are rooted at.</param>
  private static DesktopBootstrap Bootstrap(TestAppDatabase storage, MockOpenRouterServer provider,
      string workspaceRoot)
  {
    AgentSettings settings = new AgentSettings(
        new OpenRouterSettings("sk-or-test", provider.BaseUrl),
        new AgentRunOptions(null, 2)).WithWorkspaceRoot(workspaceRoot);
    SqliteAppPreferenceStore preferences = new(storage.Database);
    return new DesktopBootstrap(
        new AgentSessionFactory(settings, storage.Database),
        settings,
        Providers.OpenRouter,
        preferences,
        new IdentityProtector(),
        new SessionCatalogQueryHandler(new SqliteAgentStore(storage.Database), Providers.IsKnown),
        CommitStyle.Conventional,
        new SqliteWatchdogEventStore(storage.Database),
        WatchdogOptions.Default,
        CompactionHandoff: false,
        _ => new StubSignIn(),
        new NoRetention(),
        new SqliteRecentWorkspaceStore(storage.Database),
        PathToolsText: null,
        RetentionWindowText: null,
        GlobalSessionFiles: [],
        GlobalSkillDirectories: []);
  }

  /// <summary>A key protector that stores the key as given: this test never saves a key, and a
  ///     real protector would tie it to the machine's DPAPI state.</summary>
  private sealed class IdentityProtector : IApiKeyProtector
  {
    public string Protect(string apiKey) => apiKey;

    public string? Unprotect(string storedValue) => storedValue;
  }

  /// <summary>A retention sweep that reclaims nothing: the sweep is not what this test observes,
  ///     and a real one would open the database on a background loop during teardown.</summary>
  private sealed class NoRetention : IRetentionService
  {
    public Task<Result<RetentionRun?>> RunOnceAsync(CancellationToken ct = default)
    {
      _ = ct;
      return Task.FromResult(Result.Success<RetentionRun?>(null));
    }
  }

  /// <summary>A sign-in flow that is never exercised.</summary>
  private sealed class StubSignIn : IProviderSignIn
  {
    public Task<Result<string>> SignInAsync(CancellationToken ct = default)
        => Task.FromResult(Result.Failure<string>(new DomainError("NotExercised",
            "the sign-in flow is not exercised by this fixture.")));
  }
}
