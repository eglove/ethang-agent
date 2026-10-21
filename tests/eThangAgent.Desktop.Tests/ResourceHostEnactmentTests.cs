using System.ComponentModel;
using eThangAgent.Agent.Application;
using eThangAgent.AgentDomain;
using eThangAgent.Desktop.ViewModels;
using eThangAgent.SharedKernel;

namespace eThangAgent.Desktop.Tests;

/// <summary>The production enactment seam (spec D3/D4): what the app can honestly do
///     when the governor picks a rung. A rung the app cannot perform is REFUSED with a
///     NAMED error, and the governor records that refusal - the trail never claims an
///     action that did not happen. These refusals had no test at all, and the reclaim
///     - the one path that used to throw straight out of the governor's tick - had
///     none either.</summary>
public sealed class ResourceHostEnactmentTests
{
  [Fact]
  public async Task Steer_IsRefusedWithANamedError()
  {
    ResourceHostEnactment enactment = new(_ => new ResourceHostRegistration("c1"));

    Result<string> result = await enactment.EnactAsync("c1", ResourceAction.Steer,
        "under pressure", TestContext.Current.CancellationToken).ConfigureAwait(true);

    Assert.False(result.IsSuccess);
    Assert.Equal("RungRefused", result.Error.Code);
    Assert.Contains("steer", result.Error.Message, StringComparison.OrdinalIgnoreCase);
  }

  [Fact]
  public async Task RestartApp_IsRefusedWithANamedError()
  {
    ResourceHostEnactment enactment = new(_ => new ResourceHostRegistration("c1"));

    Result<string> result = await enactment.EnactAsync("c1", ResourceAction.RestartApp,
        "under pressure", TestContext.Current.CancellationToken).ConfigureAwait(true);

    Assert.False(result.IsSuccess);
    Assert.Equal("RungRefused", result.Error.Code);
    Assert.Contains("restart", result.Error.Message, StringComparison.OrdinalIgnoreCase);
  }

  /// <summary>The 'kill a program' rung kills the whole conversation HOST process,
  ///     so the words the user reads must say what actually happens. The domain enum
  ///     keeps its spec name (spec D3 owns the ladder); the user-facing vocabulary is
  ///     renamed, and the reason is recorded on the enactment class.</summary>
  [Fact]
  public void KillProgram_IsNamedForWhatItActuallyDoes()
  {
    // The rung is a MISNOMER in production: it stops the conversation's host
    // process, not one program inside it. The rendered phrase must say so.
    ResourceIndicatorViewModel vm = new();
    vm.Update(
        new ResourceGovernorTick(ResourceLevel.Act, "c1", ResourceAction.KillProgram,
            6_000_000_000L, 8_000_000_000L),
        [new ResourceObservation("c1", 6_000_000_000L, [])],
        [new WatchdogEvent(Guid.NewGuid(), null, WatchdogEventKind.ResourceActed,
            "conversation c1 at 6000 MB of its 8000 MB share (Act; harness budget 8000 MB) -> KillProgram",
            0, 6000, new DateTimeOffset(2026, 10, 18, 12, 0, 0, TimeSpan.Zero))]);

    string line = Assert.Single(vm.RecentDecisions);
    Assert.DoesNotContain("KillProgram", line, StringComparison.Ordinal);
    Assert.Contains("host process", line, StringComparison.OrdinalIgnoreCase);
  }

  [Fact]
  public async Task AConversationThatIsNotOpen_IsRefusedWithANamedError()
  {
    ResourceHostEnactment enactment = new(_ => null);

    Result<string> result = await enactment.EnactAsync("gone", ResourceAction.KillProgram,
        "under pressure", TestContext.Current.CancellationToken).ConfigureAwait(true);

    Assert.False(result.IsSuccess);
    Assert.Equal("ConversationNotOpen", result.Error.Code);
  }

  /// <summary>The KILL rung stops a real process. The rung is keyed by a CONVERSATION,
  ///     so the process it stops must be THAT conversation's host - and the host must
  ///     actually be gone afterwards. A rung that reported success while leaving the
  ///     process running would be a lie the audit trail would record as truth.</summary>
  [Fact]
  public async Task KillProgram_StopsTheConversationsOwnHostProcess()
  {
    using System.Diagnostics.Process host = StartChild();
    try
    {
      bool stopped = false;
      ResourceHostEnactment enactment = new(_ => new ResourceHostRegistration("c1")
      {
        HostProcessId = host.Id,
        StopHost = () =>
        {
          stopped = true;
          host.Kill(entireProcessTree: true);
        },
      });

      Result<string> result = await enactment.EnactAsync("c1", ResourceAction.KillProgram,
          "under pressure", TestContext.Current.CancellationToken).ConfigureAwait(true);

      Assert.True(result.IsSuccess);
      Assert.True(stopped, "the rung must have acted on the host process it was given");
      await host.WaitForExitAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
      Assert.True(host.HasExited, "the conversation's host process must actually be stopped");
    }
    finally
    {
      await StopChildAsync(host).ConfigureAwait(true);
    }
  }

  /// <summary>A conversation with no host process has nothing to stop, and the rung
  ///     says so by name - the trail never claims an action that did not happen.</summary>
  [Fact]
  public async Task KillProgram_WithoutAHostProcess_IsRefusedWithANamedError()
  {
    ResourceHostEnactment enactment = new(_ => new ResourceHostRegistration("c1"));

    Result<string> result = await enactment.EnactAsync("c1", ResourceAction.KillProgram,
        "under pressure", TestContext.Current.CancellationToken).ConfigureAwait(true);

    Assert.False(result.IsSuccess);
    Assert.Equal("RungRefused", result.Error.Code);
    Assert.Contains("host process", result.Error.Message, StringComparison.OrdinalIgnoreCase);
  }

  /// <summary>The RESTART rung is a distinct decision from the kill rung, and it was
  ///     untested: it must stop the conversation's own host process (the host
  ///     relaunches on the next attach) and report what it did. A restart that never
  ///     stopped anything would be a rung that reported success over an untouched
  ///     process.</summary>
  [Fact]
  public async Task RestartConversation_StopsTheConversationsOwnHostProcess()
  {
    using System.Diagnostics.Process host = StartChild();
    try
    {
      int stops = 0;
      ResourceHostEnactment enactment = new(_ => new ResourceHostRegistration("c1")
      {
        HostProcessId = host.Id,
        StopHost = () =>
        {
          stops++;
          host.Kill(entireProcessTree: true);
        },
      });

      Result<string> result = await enactment.EnactAsync("c1", ResourceAction.RestartConversation,
          "under pressure", TestContext.Current.CancellationToken).ConfigureAwait(true);

      Assert.True(result.IsSuccess);
      Assert.Equal(1, stops);
      await host.WaitForExitAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
      Assert.True(host.HasExited, "the conversation's host process must actually be stopped");
    }
    finally
    {
      await StopChildAsync(host).ConfigureAwait(true);
    }
  }

  [Fact]
  public async Task RestartConversation_WithoutAHostProcess_IsRefusedWithANamedError()
  {
    ResourceHostEnactment enactment = new(_ => new ResourceHostRegistration("c1"));

    Result<string> result = await enactment.EnactAsync("c1", ResourceAction.RestartConversation,
        "under pressure", TestContext.Current.CancellationToken).ConfigureAwait(true);

    Assert.False(result.IsSuccess);
    Assert.Equal("RungRefused", result.Error.Code);
  }

  /// <summary>THE RECLAIM, AND THE DEFECT IT CARRIED. The old recipe set the host's
  ///     minimum and maximum working set to ZERO, which the OS rejects: a zero
  ///     MINIMUM raises Win32Exception (ERROR_INVALID_PARAMETER) and a zero MAXIMUM
  ///     raises ArgumentException. Neither was in the seam's catch list, so the
  ///     exception ESCAPED the enactment, then the governor's tick, and was swallowed
  ///     by the governor loop's broad catch: the tick vanished, the audit trail
  ///     recorded NO refusal row, and the user saw nothing. This test drives the real
  ///     process and asserts the reclaim SUCCEEDS - the defect's direction, because a
  ///     reclaim that throws is a reclaim that never happens.</summary>
  [Fact]
  public async Task ReclaimMemory_TrimsARealHostProcess_AndSucceeds()
  {
    using System.Diagnostics.Process host = StartChild();
    try
    {
      long before = host.WorkingSet64;
      ResourceHostEnactment enactment = new(_ => new ResourceHostRegistration("c1")
      {
        HostProcessId = host.Id,
      });

      Result<string> result = await enactment.EnactAsync("c1", ResourceAction.ReclaimMemory,
          "under pressure", TestContext.Current.CancellationToken).ConfigureAwait(true);

      Assert.True(result.IsSuccess, "the reclaim must not fail: " + result.Error?.Message);
      host.Refresh();
      Assert.True(host.WorkingSet64 < before,
          $"the reclaim must actually release pages (before {before}, after {host.WorkingSet64})");
    }
    finally
    {
      await StopChildAsync(host).ConfigureAwait(true);
    }
  }

  /// <summary>The reclaim must leave the host's OWN limits as it found them. A reclaim
  ///     that capped the conversation's memory at the reclaim's ceiling would trade a
  ///     memory problem for a permanently throttled conversation - a far worse fault
  ///     than the one the reclaim fixes.</summary>
  [Fact]
  public async Task ReclaimMemory_RestoresTheHostsOwnWorkingSetLimits()
  {
    using System.Diagnostics.Process host = StartChild();
    try
    {
      nint originalMin = host.MinWorkingSet;
      nint originalMax = host.MaxWorkingSet;
      ResourceHostEnactment enactment = new(_ => new ResourceHostRegistration("c1")
      {
        HostProcessId = host.Id,
      });

      Result<string> result = await enactment.EnactAsync("c1", ResourceAction.ReclaimMemory,
          "under pressure", TestContext.Current.CancellationToken).ConfigureAwait(true);

      Assert.True(result.IsSuccess);
      host.Refresh();
      Assert.Equal(originalMin, host.MinWorkingSet);
      Assert.Equal(originalMax, host.MaxWorkingSet);
    }
    finally
    {
      await StopChildAsync(host).ConfigureAwait(true);
    }
  }

  /// <summary>The defect's exact shape, MEASURED: a host this account may not inspect
  ///     raises a Win32Exception out of the working-set write. The seam must turn it
  ///     into a NAMED failure, never let it escape - the escape is what swallowed the
  ///     tick and left the audit trail empty. The process is the SYSTEM process: it
  ///     exists, and no unprivileged account may open it.</summary>
  [Fact]
  public async Task ReclaimMemory_OnAProcessTheAppMayNotInspect_FailsNamed_AndNeverThrows()
  {
    ResourceHostEnactment enactment = new(_ => new ResourceHostRegistration("c1")
    {
      HostProcessId = UntouchableProcessId(),
    });

    // The assertion IS the defect: before the fix this call THREW the Win32Exception
    // out of the enactment instead of returning a failure.
    Result<string> result = await enactment.EnactAsync("c1", ResourceAction.ReclaimMemory,
        "under pressure", TestContext.Current.CancellationToken).ConfigureAwait(true);

    Assert.False(result.IsSuccess);
    Assert.Equal("ReclaimFailed", result.Error.Code);
  }

  /// <summary>A host that has exited is the SAME class of failure: the OS raises an
  ///     InvalidOperationException out of the working-set write, and the seam must
  ///     name it rather than let it escape the tick.</summary>
  [Fact]
  public async Task ReclaimMemory_OnAHostThatHasExited_FailsNamed_AndNeverThrows()
  {
    using System.Diagnostics.Process host = StartChild();
    try
    {
      host.Kill(entireProcessTree: true);
      await host.WaitForExitAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
      ResourceHostEnactment enactment = new(_ => new ResourceHostRegistration("c1")
      {
        HostProcessId = host.Id,
      });

      Result<string> result = await enactment.EnactAsync("c1", ResourceAction.ReclaimMemory,
          "under pressure", TestContext.Current.CancellationToken).ConfigureAwait(true);

      Assert.False(result.IsSuccess);
      Assert.Equal("ReclaimFailed", result.Error.Code);
    }
    finally
    {
      await StopChildAsync(host).ConfigureAwait(true);
    }
  }

  /// <summary>A conversation with no host process has nothing to reclaim, and the rung
  ///     says so by name.</summary>
  [Fact]
  public async Task ReclaimMemory_WithoutAHostProcess_FailsNamed()
  {
    ResourceHostEnactment enactment = new(_ => new ResourceHostRegistration("c1"));

    Result<string> result = await enactment.EnactAsync("c1", ResourceAction.ReclaimMemory,
        "under pressure", TestContext.Current.CancellationToken).ConfigureAwait(true);

    Assert.False(result.IsSuccess);
    Assert.Equal("ReclaimFailed", result.Error.Code);
  }

  /// <summary>The reclaim never writes a zero limit. A zero minimum is rejected by the
  ///     OS with ERROR_INVALID_PARAMETER and a zero maximum is rejected outright - the
  ///     old recipe could therefore only ever throw, so the reclaim NEVER happened.
  ///     The seam's own floors are pinned here, because a regression to zero would be
  ///     invisible in a unit test (a zero would still succeed against a fake) and
  ///     fatal in production.</summary>
  [Fact]
  public void TheReclaimsFloorsAndCeiling_AreUsableByTheOperatingSystem()
  {
    // The OS's own rule: a maximum must be at least the minimum, and neither may be
    // zero for a working-set write.
    Assert.True(ResourceHostEnactment.TrimMinimumBytes > 0);
    Assert.True(ResourceHostEnactment.TrimMaximumBytes > 0);
    Assert.True(ResourceHostEnactment.TrimMaximumBytes >= ResourceHostEnactment.TrimMinimumBytes);
  }

  /// <summary>The reclaim's floor and ceiling are actually ACCEPTED by the operating
  ///     system for a real process - the measurement the old recipe failed. A fake
  ///     cannot show this: only the OS can reject a limit.</summary>
  [Fact]
  public async Task TheReclaimsLimits_AreAcceptedByTheOperatingSystem()
  {
    using System.Diagnostics.Process host = StartChild();
    try
    {
      nint originalMin = host.MinWorkingSet;
      nint originalMax = host.MaxWorkingSet;
      try
      {
        host.MinWorkingSet = ResourceHostEnactment.TrimMinimumBytes;
        host.MaxWorkingSet = ResourceHostEnactment.TrimMaximumBytes;
      }
      finally
      {
        host.MaxWorkingSet = originalMax;
        host.MinWorkingSet = originalMin;
      }
    }
    finally
    {
      await StopChildAsync(host).ConfigureAwait(true);
    }
  }

  /// <summary>A live, long-lived child process with a real working set. The system's
  ///     own ping is used: it is present on every Windows install, it stays alive on
  ///     its own, and it holds a real resident set for the reclaim to release.
  ///     <para>BOTH pipes are redirected on purpose: an unredirected child inherits
  ///     the runner's stderr handle and keeps it open, which stalls the test host's
  ///     exit. The child is killed on dispose, so a run never leaks processes.</para></summary>
  private static System.Diagnostics.Process StartChild()
  {
    string ping = Path.Combine(Environment.SystemDirectory, "ping.exe");
    Assert.True(File.Exists(ping), "the system ping must be present for this test");
    System.Diagnostics.Process child = System.Diagnostics.Process.Start(
        new System.Diagnostics.ProcessStartInfo(ping, "-t 127.0.0.1")
        {
          UseShellExecute = false,
          CreateNoWindow = true,
          RedirectStandardOutput = true,
          RedirectStandardError = true,
        })!;
    // Let it settle: a process that has not yet faulted its pages in has nothing to
    // reclaim, and the assertion would measure the ramp rather than the trim.
    Thread.Sleep(700);
    return child;
  }

  /// <summary>Kills the child and waits for it, so a test never leaves a process
  ///     behind. Best effort: a process that has already exited is the desired end
  ///     state.</summary>
  private static async Task StopChildAsync(System.Diagnostics.Process child)
  {
    try
    {
      if (!child.HasExited)
      {
        child.Kill(entireProcessTree: true);
      }
    }
    catch (InvalidOperationException)
    {
      // The process exited between the check and the kill.
    }

    await child.WaitForExitAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
  }

  /// <summary>A process id that EXISTS but that an unprivileged account may not open:
  ///     the OS raises a Win32Exception (Access is denied) for it. The SYSTEM process
  ///     is the stable choice on Windows.</summary>
  private static int UntouchableProcessId()
  {
    const int SystemProcessId = 4;
    using System.Diagnostics.Process system = System.Diagnostics.Process.GetProcessById(SystemProcessId);
    _ = Assert.Throws<Win32Exception>(() => system.Handle);
    return SystemProcessId;
  }
}
