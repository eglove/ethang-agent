using System.Text.Json;
using eThangAgent.AgentDomain;
using eThangAgent.AgentInfrastructure;
using eThangAgent.ModelDomain;
using eThangAgent.ToolDomain;
using eThangAgent.Transport.ACL;
using Microsoft.Extensions.DependencyInjection;

namespace eThangAgent.Composition.Tests;

/// <summary>Task 10 (spec D6/D7): every conversation runs in its OWN host process. The
///     pipe name and the scratch folder both derive from the CONVERSATION id, so two
///     conversations opened in the same workspace get two hosts and two budgets instead
///     of fighting over one pipe and one settings file. Both derivations must be STABLE
///     across app restarts, so a relaunching app FINDS a live host instead of spawning a
///     second one. The carry-forward pin (task 9 left AgentSettings.SessionId with no
///     writer): the supervisor stamps the conversation id into the settings JSON, because
///     the host resolves its root runner ONLY when SessionId is set - without the stamp
///     every root turn answers RootTurnUnavailable.</summary>
public class ConversationHostSupervisionTests
{
  private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);

  [Fact]
  public void PipeName_TwoConversationsInOneWorkspace_AreDistinct()
  {
    string first = RemoteHostSupervisor.PipeNameForConversation("conversation-a");
    string second = RemoteHostSupervisor.PipeNameForConversation("conversation-b");

    Assert.NotEqual(first, second);
  }

  [Fact]
  public void PipeName_SameConversation_IsStableAcrossCalls()
  {
    Assert.Equal(
        RemoteHostSupervisor.PipeNameForConversation("conversation-a"),
        RemoteHostSupervisor.PipeNameForConversation("conversation-a"));
  }

  [Fact]
  public void PipeName_IsFilesystemAndPipeSafe()
  {
    string name = RemoteHostSupervisor.PipeNameForConversation(@"C:\projects\some workspace");

    Assert.DoesNotContain('\\', name);
    Assert.DoesNotContain('/', name);
    Assert.DoesNotContain(':', name);
    Assert.DoesNotContain(' ', name);
  }

  [Fact]
  public void ScratchFolder_TwoConversationsInOneWorkspace_AreDistinct()
  {
    string first = RemoteHostSupervisor.ScratchFolderForConversation("conversation-a");
    string second = RemoteHostSupervisor.ScratchFolderForConversation("conversation-b");

    Assert.NotEqual(first, second);
  }

  [Fact]
  public async Task Supervisor_StampsTheConversationIdIntoTheHostSettingsJson()
  {
    string scratch = Directory.CreateTempSubdirectory("ethang-conv-scratch").FullName;
    try
    {
      string workspace = Directory.CreateTempSubdirectory("ethang-conv-ws").FullName;
      try
      {
        AgentSettings settings = new AgentSettings(
            new OpenRouterSettings("sk-test", new Uri("https://openrouter.test")),
            new SubAgentOptions(null, 2)).WithWorkspaceRoot(workspace);
        RemoteHostSupervisor supervisor = new("conversation-a", scratch,
            settings, Path.Combine(scratch, "app.db"), _ => { });
        try
        {
          string json = await File.ReadAllTextAsync(
              Path.Combine(scratch, "childhost-settings.json"),
              TestContext.Current.CancellationToken).ConfigureAwait(true);
          AgentSettings shipped = JsonSerializer.Deserialize<AgentSettings>(json, WebJson)!;

          Assert.Equal("conversation-a", shipped.SessionId);
        }
        finally
        {
          await supervisor.DisposeAsync().ConfigureAwait(true);
        }
      }
      finally
      {
        Directory.Delete(workspace, recursive: true);
      }
    }
    finally
    {
      Directory.Delete(scratch, recursive: true);
    }
  }

  [Fact]
  public async Task Supervisor_KeepsTheWorkspaceRootItWasGiven()
  {
    string scratch = Directory.CreateTempSubdirectory("ethang-conv-scratch2").FullName;
    try
    {
      string workspace = Directory.CreateTempSubdirectory("ethang-conv-ws2").FullName;
      try
      {
        AgentSettings settings = new AgentSettings(
            new OpenRouterSettings("sk-test", new Uri("https://openrouter.test")),
            new SubAgentOptions(null, 2)).WithWorkspaceRoot(workspace);
        RemoteHostSupervisor supervisor = new("conversation-a", scratch,
            settings, Path.Combine(scratch, "app.db"), _ => { });
        try
        {
          string json = await File.ReadAllTextAsync(
              Path.Combine(scratch, "childhost-settings.json"),
              TestContext.Current.CancellationToken).ConfigureAwait(true);
          AgentSettings shipped = JsonSerializer.Deserialize<AgentSettings>(json, WebJson)!;

          Assert.Equal(workspace, shipped.WorkspaceRoot);
        }
        finally
        {
          await supervisor.DisposeAsync().ConfigureAwait(true);
        }
      }
      finally
      {
        Directory.Delete(workspace, recursive: true);
      }
    }
    finally
    {
      Directory.Delete(scratch, recursive: true);
    }
  }

  [Fact]
  public void Core_WiresTheWireRuntime_WithNoToggle()
  {
    // Spec D6/D7: there is exactly ONE way the app runs a conversation — its own host
    // process, driven through the wire runtime. No settings flag selects it, and the
    // in-process runtime is not registered by the core at all.
    using TestAppDatabase db = TestAppDatabase.Create();
    using ServiceProvider services = new ServiceCollection()
        .AddEThangAgentCore(
            new AgentSettings(
                new OpenRouterSettings("sk-or-test", new Uri("https://openrouter.test")),
                new SubAgentOptions(null, 2)),
            Providers.OpenRouter,
            ModelConfig.Create("test/model", null, 512, 0.5f, 8192).Value!,
            new AgentHostOptions(new FixedWorkspaceContext("app"), new UnrootedPathResolver()),
            db.Database)
        .BuildServiceProvider();

    _ = Assert.IsType<RemoteAgentRuntime>(services.GetRequiredService<IAgentRuntime>());
    Assert.Null(services.GetService<InProcessAgentRuntime>());
  }

  [Fact]
  public async Task Core_HostSupervisor_KeysOnThePublishedConversationId()
  {
    using TestAppDatabase db = TestAppDatabase.Create();
    ServiceProvider services = new ServiceCollection()
        .AddEThangAgentCore(
            new AgentSettings(
                new OpenRouterSettings("sk-or-test", new Uri("https://openrouter.test")),
                new SubAgentOptions(null, 2)),
            Providers.OpenRouter,
            ModelConfig.Create("test/model", null, 512, 0.5f, 8192).Value!,
            new AgentHostOptions(new FixedWorkspaceContext("app"), new UnrootedPathResolver()),
            db.Database)
        .BuildServiceProvider();

    // Before the factory publishes the root id there is no conversation to key on: the
    // supervisor refuses LOUDLY rather than launching a host against a guessed id.
    InvalidOperationException unpublished = Assert.Throws<InvalidOperationException>(
        services.GetRequiredService<RemoteHostSupervisor>);
    Assert.Contains("root session id", unpublished.Message, StringComparison.Ordinal);

    AgentId conversation = AgentId.NewId();
    services.GetRequiredService<RootSessionIdentity>().Id = conversation;
    // The supervisor is IAsyncDisposable, so the container must be disposed the way the
    // session factory disposes it (DisposeAsync), never synchronously.
    await using (services.ConfigureAwait(false))
    {
      RemoteHostSupervisor supervisor = services.GetRequiredService<RemoteHostSupervisor>();

      Assert.Equal(RemoteHostSupervisor.PipeNameForConversation(conversation.ToString()),
          supervisor.HostPipeName);
    }
  }

  [Fact]
  public void Supervisor_WithoutAWorkspaceRoot_FailsNamed()
  {
    string scratch = Directory.CreateTempSubdirectory("ethang-conv-scratch3").FullName;
    try
    {
      AgentSettings settings = new(
          new OpenRouterSettings("sk-test", new Uri("https://openrouter.test")),
          new SubAgentOptions(null, 2));

      InvalidOperationException error = Assert.Throws<InvalidOperationException>(
          () => new RemoteHostSupervisor("conversation-a", scratch,
              settings, Path.Combine(scratch, "app.db"), _ => { }));

      Assert.Contains("WorkspaceRoot", error.Message, StringComparison.Ordinal);
    }
    finally
    {
      Directory.Delete(scratch, recursive: true);
    }
  }
  // --- The startup bound is a CLOCK, not a line counter (task 10) ---

  /// <summary>A line source that serves the given lines in order, then repeats the last.
  ///     A null entry means stdout closed.</summary>
  private static Func<CancellationToken, ValueTask<string?>> LinesFrom(params string?[] lines)
  {
    Queue<string?> queue = new(lines);
    string? last = lines.Length == 0 ? null : lines[^1];
    return _ => new ValueTask<string?>(queue.Count > 0 ? queue.Dequeue() : last);
  }

  /// <summary>A line source that blocks until its token fires.</summary>
  private static async ValueTask<string?> BlocksUntilCancelled(CancellationToken token)
  {
    await Task.Delay(Timeout.Infinite, token).ConfigureAwait(false);
    return null;
  }

  /// <summary>The hang this pins: the wait used to be bounded by a LINE COUNTER, and its
  ///     first read was awaited BEFORE the counter could advance - so a host that started
  ///     and printed nothing blocked the attach forever. Task 10 turned that rare hang into
  ///     a routine one (every conversation attaches). This test FAILS on the counter shape
  ///     instead of hanging the suite: the read never completes, and the assertion bound is
  ///     far shorter than any suite timeout.</summary>
  [Fact]
  public async Task StartedLine_SilentHost_TimesOutInsteadOfBlockingForever()
  {
    Task<RemoteHostSupervisor.StartedLineOutcome> wait = RemoteHostSupervisor.AwaitStartedLineAsync(
        BlocksUntilCancelled,
        () => false,
        TimeSpan.FromMilliseconds(200),
        TestContext.Current.CancellationToken);

    // The bound: a regression cannot hang the suite, it fails here.
    Task finished = await Task.WhenAny(
        wait, Task.Delay(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken))
        .ConfigureAwait(true);
    Assert.Same(wait, finished);
    Assert.Equal(RemoteHostSupervisor.StartedLineOutcome.TimedOut, await wait.ConfigureAwait(true));
  }

  [Fact]
  public async Task StartedLine_AnnouncedLine_ResolvesAnnounced()
  {
    RemoteHostSupervisor.StartedLineOutcome outcome = await RemoteHostSupervisor.AwaitStartedLineAsync(
        LinesFrom("host-starting ethang-conv-ABCD1234"),
        () => false,
        TimeSpan.FromSeconds(5),
        TestContext.Current.CancellationToken).ConfigureAwait(true);

    Assert.Equal(RemoteHostSupervisor.StartedLineOutcome.Announced, outcome);
  }

  [Fact]
  public async Task StartedLine_NoiseThenAnnouncement_KeepsReadingUntilTheMarker()
  {
    RemoteHostSupervisor.StartedLineOutcome outcome = await RemoteHostSupervisor.AwaitStartedLineAsync(
        LinesFrom("noise-1", "noise-2", "host-starting ethang-conv-ABCD1234"),
        () => false,
        TimeSpan.FromSeconds(5),
        TestContext.Current.CancellationToken).ConfigureAwait(true);

    Assert.Equal(RemoteHostSupervisor.StartedLineOutcome.Announced, outcome);
  }

  [Fact]
  public async Task StartedLine_ClosedStdoutWithoutAnnouncement_ResolvesAtOnce()
  {
    // A null line means stdout is closed: the host can never announce itself, so the
    // wait must resolve immediately rather than spin until the deadline.
    RemoteHostSupervisor.StartedLineOutcome outcome = await RemoteHostSupervisor.AwaitStartedLineAsync(
        LinesFrom((string?)null),
        () => true,
        TimeSpan.FromSeconds(30),
        TestContext.Current.CancellationToken).ConfigureAwait(true);

    Assert.Equal(RemoteHostSupervisor.StartedLineOutcome.Exited, outcome);
  }

  [Fact]
  public async Task StartedLine_CallerCancellation_Propagates()
  {
    using CancellationTokenSource cancelled = new();
    await cancelled.CancelAsync().ConfigureAwait(true);

    _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(
        () => RemoteHostSupervisor.AwaitStartedLineAsync(
            BlocksUntilCancelled,
            () => false,
            TimeSpan.FromSeconds(30),
            cancelled.Token)).ConfigureAwait(true);
  }

  /// <summary>The connect bound (review finding 3): TryConnectAsync must return null - not
  ///     block - when no host answers inside its deadline. A pipe whose only instance is
  ///     held leaves NamedPipeClientStream.ConnectAsync retrying forever, so an unbounded
  ///     probe is the second suite-wide hang. This test FAILS on an unbounded probe
  ///     instead of hanging: the assertion bound is far shorter than any suite timeout.</summary>
  [Fact]
  public async Task Probe_NoHostListening_ReturnsNullInsideItsDeadline()
  {
    string scratch = Directory.CreateTempSubdirectory("ethang-probe-scratch").FullName;
    try
    {
      string workspace = Directory.CreateTempSubdirectory("ethang-probe-ws").FullName;
      try
      {
        AgentSettings settings = new AgentSettings(
            new OpenRouterSettings("sk-test", new Uri("https://openrouter.test")),
            new SubAgentOptions(null, 2)).WithWorkspaceRoot(workspace);
        // A pipe name nothing serves: the probe must give up, not wait forever.
        RemoteHostSupervisor supervisor = new("conversation-probe", scratch,
            settings, Path.Combine(scratch, "app.db"), _ => { });

        Task<NamedPipeChildTransport?> probe = supervisor.ProbeForTestAsync(TimeSpan.FromMilliseconds(300),
            TestContext.Current.CancellationToken);
        Task finished = await Task.WhenAny(
            probe, Task.Delay(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken))
            .ConfigureAwait(true);
        Assert.Same(probe, finished);
        Assert.Null(await probe.ConfigureAwait(true));
        await supervisor.DisposeAsync().ConfigureAwait(true);
      }
      finally
      {
        Directory.Delete(workspace, recursive: true);
      }
    }
    finally
    {
      Directory.Delete(scratch, recursive: true);
    }
  }

  /// <summary>The connect bound itself is a CLOCK with a sane value: a regression to an
  ///     unbounded (or absurd) connect is caught by the pin, not by a hung suite.</summary>
  [Fact]
  public void ConnectTimeout_IsABoundedClock()
  {
    Assert.True(RemoteHostSupervisor.ConnectTimeout > TimeSpan.Zero);
    Assert.True(RemoteHostSupervisor.ConnectTimeout <= TimeSpan.FromSeconds(60));
    Assert.True(RemoteHostSupervisor.ProbeTimeout > TimeSpan.Zero);
    Assert.True(RemoteHostSupervisor.ProbeTimeout < RemoteHostSupervisor.ConnectTimeout,
        "the reuse probe must be shorter than a full connect, or reuse would cost a launch wait");
  }

  /// <summary>Closed stdout WITHOUT an exit (review finding 6): the host closed its pipe but
  ///     is still alive. A null line means it can never announce itself, so the wait must
  ///     resolve at once as TimedOut rather than spin to the deadline. The earlier
  ///     hasExited:true test never entered the loop body, so this production shape was
  ///     unexercised.</summary>
  [Fact]
  public async Task StartedLine_ClosedStdoutWhileAlive_ResolvesWithoutWaitingForTheDeadline()
  {
    System.Diagnostics.Stopwatch elapsed = System.Diagnostics.Stopwatch.StartNew();
    RemoteHostSupervisor.StartedLineOutcome outcome = await RemoteHostSupervisor.AwaitStartedLineAsync(
        LinesFrom((string?)null),
        () => false,
        TimeSpan.FromSeconds(30),
        TestContext.Current.CancellationToken).ConfigureAwait(true);
    elapsed.Stop();

    Assert.Equal(RemoteHostSupervisor.StartedLineOutcome.TimedOut, outcome);
    Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(10),
        $"closed stdout must resolve at once, not wait out the 30s deadline (took {elapsed.Elapsed})");
  }

  /// <summary>The startup deadline is pinned TIGHTLY (review finding 7): the old
  ///     a five-minute ceiling let a five-minute regression pass.</summary>
  [Fact]
  public void StartedLineTimeout_IsPinnedTightly() =>
      Assert.Equal(TimeSpan.FromSeconds(60), RemoteHostSupervisor.StartedLineTimeout);
}
