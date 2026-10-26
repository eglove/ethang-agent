using System.Diagnostics;
using System.Reflection;
using eThangAgent.AgentDomain;
using eThangAgent.TestSupport;
using Microsoft.Extensions.DependencyInjection;

namespace eThangAgent.Composition.Tests;

/// <summary>Issue #287: <see cref="RemoteHostSupervisor.HostProcessId"/> documents null as
///     "an honest 'no host right now', never a fabricated id" — and after disposal it THREW
///     instead. A disposed <see cref="Process"/> answers <c>HasExited</c> with
///     <c>InvalidOperationException</c> ("No process is associated with this object.") rather
///     than true, so the getter's guard could not short-circuit: the guard itself was the
///     exception. The consumer this contract exists for is exactly the post-mortem one —
///     something sampling a supervisor after its session closed — so the failure landed
///     only where the property was supposed to be useful.
///     <para>These are the issue's own reproduction, as assertions, over a REAL composed
///     session with a REAL host process: one case with the host still running at disposal,
///     one with it already exited (which the old dispose left holding an undisposed handle
///     for the supervisor's whole lifetime).</para>
///     <para>Derives from <see cref="SessionOwningTest"/> (issue #284 item 3): a session owns
///     a host process, so teardown runs from <c>DisposeAsync</c> and a failing assertion
///     cannot skip it. The tests here dispose the session THEMSELVES mid-test, which is the
///     condition under test; the base class disposes it again afterwards, and a second
///     disposal is a no-op by the same contract.</para></summary>
[Collection("EnvironmentSensitive")]
public class RemoteHostSupervisorDisposalTests : SessionOwningTest
{
  private static AgentSettings Settings() => new(
      new OpenRouterSettings("sk-or-test", new Uri("https://openrouter.test")),
      new SubAgentOptions(null, 2));

  private static RemoteHostSupervisor SupervisorOf(AgentSession session) =>
      session.Services.GetRequiredService<RemoteHostSupervisor>();

  /// <summary>The supervisor's current Process, read by reflection: releasing the handle is
  ///     an acceptance criterion with no public surface, and widening production for a test
  ///     is the wrong trade — the same reasoning HostContainmentTests records for JobObject.</summary>
  private static Process CapturedHost(RemoteHostSupervisor supervisor) =>
      (Process)typeof(RemoteHostSupervisor)
          .GetProperty("Host", BindingFlags.NonPublic | BindingFlags.Instance)!
          .GetValue(supervisor)!;

  /// <summary>A disposed Process faults on EVERY member, which is what makes handle release
  ///     observable: the captured instance throws only if disposal actually ran.</summary>
  private static bool WasDisposed(Process captured)
  {
    try
    {
      _ = captured.HasExited;
      return false;
    }
    catch (InvalidOperationException)
    {
      return true;
    }
  }

  /// <summary>The reproduction from the issue verbatim: read the id, dispose, read it again.
  ///     Before the fix the second read threw out of <c>HasExited</c>; the contract says it
  ///     answers null, because a closed session has no host — which is not an error.</summary>
  [Fact]
  public async Task HostProcessId_AfterDispose_ReturnsNull()
  {
    AgentSession session = await CreateSessionAsync(Settings()).ConfigureAwait(true);
    RemoteHostSupervisor supervisor = SupervisorOf(session);

    int? live = supervisor.HostProcessId;
    Assert.True(live is > 0, "an open session must own a live host process");
    Process captured = CapturedHost(supervisor);

    await session.Services.DisposeAsync().ConfigureAwait(true);

    Assert.Null(supervisor.HostProcessId);
    Assert.True(WasDisposed(captured), "disposal must release the running host's handle");
  }


  /// <summary>DISPOSAL MEANS THE HOST IS GONE, NOT THAT A KILL WAS REQUESTED.
  ///     <para>The defect this pins: <c>Process.Kill</c> returns as soon as the kernel
  ///     accepts the request, while the host keeps running and keeps holding its SQLite
  ///     file, pipe and scratch folder for a window afterwards. Measured on a loaded
  ///     machine, test-spawned hosts were still executing turns seconds after the
  ///     container that owned them had been disposed - so a caller that treated
  ///     'dispose completed' as 'this session's host is gone' observed a live process it
  ///     believed it had killed. The Desktop suite's leak guard is exactly such a caller,
  ///     and it reported correctly-finished tests as leaks.</para>
  ///     <para>What is asserted is the CONTRACT rather than a duration: when disposal
  ///     returns, the process id is no longer present in the process table. The bound
  ///     lives in the supervisor (<c>ExitWaitTimeout</c>) so a wedged host cannot hang
  ///     teardown; this test would fail on the pre-fix code, where disposal returned while
  ///     the host was still alive.</para></summary>
  [Fact]
  public async Task DisposeAsync_DoesNotReturnUntilTheHostProcessIsGone()
  {
    AgentSession session = await CreateSessionAsync(Settings()).ConfigureAwait(true);
    RemoteHostSupervisor supervisor = SupervisorOf(session);

    int pid = supervisor.HostProcessId
        ?? throw new InvalidOperationException("the session must own a live host process");
    Assert.True(IsPresent(pid), "the host must be running before disposal");

    await session.Services.DisposeAsync().ConfigureAwait(true);

    Assert.False(IsPresent(pid),
        $"disposal returned while host {pid} was still in the process table - a caller cannot "
        + "distinguish that from a leak");
  }

  /// <summary>Is this pid in the process table at all? A presence question, deliberately
  ///     NOT a liveness one: the leak guard (and any caller asking 'is it still here?')
  ///     enumerates processes, so a terminated-but-present process still costs it an
  ///     answer. Asking presence makes the assertion strict rather than generous.</summary>
  private static bool IsPresent(int pid)
  {
    foreach (Process candidate in Process.GetProcesses())
    {
      using (candidate)
      {
        try
        {
          if (candidate.Id == pid)
          {
            return true;
          }
        }
        catch (InvalidOperationException)
        {
          // Exited between enumeration and the read: not present any more.
        }
      }
    }

    return false;
  }
  /// <summary>The other half of the acceptance criteria: a host that had ALREADY exited when
  ///     the supervisor was disposed. The old dispose only touched the process when
  ///     <c>HasExited</c> was false, so this case kept an undisposed handle for the
  ///     supervisor's lifetime — pinned here by the captured instance, which must fault
  ///     after disposal. Teardown must also stay idempotent, because the reference is
  ///     cleared: a second disposal is a no-op rather than a read through a dead handle.</summary>
  [Fact]
  public async Task DisposeAsync_WhenTheHostAlreadyExited_ReleasesTheHandleAndReturnsNull()
  {
    AgentSession session = await CreateSessionAsync(Settings()).ConfigureAwait(true);
    RemoteHostSupervisor supervisor = SupervisorOf(session);

    int pid = supervisor.HostProcessId
        ?? throw new InvalidOperationException("the session must own a live host process");
    Process captured = CapturedHost(supervisor);
    using (Process host = Process.GetProcessById(pid))
    {
      host.Kill(entireProcessTree: true);
      await host.WaitForExitAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
    }

    await session.Services.DisposeAsync().ConfigureAwait(true);

    Assert.Null(supervisor.HostProcessId);
    Assert.True(WasDisposed(captured), "disposal must release an already-exited host's handle");

    await supervisor.DisposeAsync().ConfigureAwait(true);
    Assert.Null(supervisor.HostProcessId);
  }
}
