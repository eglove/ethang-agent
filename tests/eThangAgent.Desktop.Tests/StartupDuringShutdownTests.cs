using eThangAgent.AgentDomain;
using eThangAgent.Composition;
using eThangAgent.Desktop.ViewModels;
using eThangAgent.SharedKernel;
using eThangAgent.TestSupport;

namespace eThangAgent.Desktop.Tests;

/// <summary>Issue #368: a session start that is still building when the app shuts down cannot
///     outlive the shutdown.
///     <para>THE DEFECT THIS PINS. A start's container is built off the UI thread, and the
///     container owns a conversation host process from the moment it exists - BEFORE any cell
///     names it. <c>ShutdownAllAsync</c> walked the tabs and returned, so a start still in
///     flight was invisible to it: the session finished building after the walk, appended a
///     cell into a shell nobody would ever close again, and its host stayed alive. The suite's
///     leak guard found it, correctly, as a host no supervisor had ever been asked to exit.</para>
///     <para>WHAT IS ASSERTED IS THE CONTRACT: when <c>ShutdownAllAsync</c> returns, the
///     container of a start that was in flight is disposed. The start is held open by a
///     creation delegate the test controls, so the race is deterministic instead of a timing
///     accident - and the pre-fix code fails here, because the walk ran while the container did
///     not yet exist.</para></summary>
public class StartupDuringShutdownTests
{
  /// <summary>A start held open across the shutdown is disposed by the shutdown's own wait.
  ///     <para>THE ORDER IS THE TEST. The start is begun, the creation delegate is parked, the
  ///     shutdown is begun (it must wait), and only then is the session released - so the walk
  ///     and the start genuinely overlap.</para></summary>
  [Fact]
  public async Task ShutdownAllAsync_DisposesASessionWhoseStartWasStillInFlight()
  {
    TaskCompletionSource<Result<AgentSession>> release = new(TaskCreationOptions.RunContinuationsAsynchronously);
    TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
    AgentSession? built = null;
    MainViewModel shell = new(createSession: (_, _) =>
    {
      _ = started.TrySetResult();
      return HoldAsync(release, session => built = session);
    });

    Task<Result<AgentTabViewModel>> opening = shell.OpenAgentAsync(
        Directory.CreateTempSubdirectory("ethang-open-race").FullName, Providers.OpenRouter);
    await started.Task.WaitAsync(TestCeiling.Value, TestContext.Current.CancellationToken)
        .ConfigureAwait(true);

    // THE SHUTDOWN BEGINS WHILE THE CONTAINER IS STILL BUILDING. It must not return yet: the
    // session it cannot see is the one about to own a host process.
    Task shutdown = Task.Run(shell.ShutdownAllAsync, TestContext.Current.CancellationToken);
    await Task.Delay(TimeSpan.FromMilliseconds(50), TestContext.Current.CancellationToken)
        .ConfigureAwait(true);
    Assert.False(shutdown.IsCompleted,
        "shutdown returned while a session start was still building - the host it will own "
        + "would outlive the app.");

    AgentSession session = FakeSession();
    _ = release.TrySetResult(Result.Success(session));
    await shutdown.WaitAsync(TestCeiling.Value, TestContext.Current.CancellationToken).ConfigureAwait(true);
    Result<AgentTabViewModel> opened = await opening.WaitAsync(TestCeiling.Value,
        TestContext.Current.CancellationToken).ConfigureAwait(true);

    // THE START DID NOT LAND IN A CLOSING SHELL, and its container is disposed either way.
    Assert.False(opened.IsSuccess);
    Assert.Empty(shell.Tabs);
    Assert.True(built is not null, "the creation delegate must have produced the session");
    Assert.True(await IsDisposedAsync(built).ConfigureAwait(true),
        "a session started during shutdown must have its container disposed, or its host "
        + "process outlives the app.");
  }

  /// <summary>Holds the creation open until <paramref name="release"/> settles, then answers
  ///     with what the test put there. The session is captured so the assertion can ask whether
  ///     its container was disposed.</summary>
  private static async Task<Result<AgentSession>> HoldAsync(
      TaskCompletionSource<Result<AgentSession>> release, Action<AgentSession> capture)
  {
    Result<AgentSession> result = await release.Task.ConfigureAwait(false);
    if (result.IsSuccess)
    {
      capture(result.Value);
    }

    return result;
  }

  /// <summary>True when the session's container has been disposed: resolving a service from a
  ///     disposed provider is the one observable that says so.</summary>
  private static Task<bool> IsDisposedAsync(AgentSession session)
  {
    try
    {
      _ = session.Services.GetService(typeof(IAgentStore));
      return Task.FromResult(false);
    }
    catch (ObjectDisposedException)
    {
      return Task.FromResult(true);
    }
  }

  /// <summary>A session over a throwaway container - the smallest thing a shell can be handed.
  ///     </summary>
  private static AgentSession FakeSession()
  {
    AgentSessionViewModel vm = TestFixtures.CreateViewModel();
    return TestFixtures.FakeSessionFor(vm);
  }
}
