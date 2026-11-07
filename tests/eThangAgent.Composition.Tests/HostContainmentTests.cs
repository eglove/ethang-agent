using System.Diagnostics;
using System.Reflection;

namespace eThangAgent.Composition.Tests;

/// <summary>The kernel half of issue #274's containment guarantee: a process assigned to a
///     kill-on-close Job Object dies when the handle closes - which is exactly what the OS
///     does to every handle a process holds when that process exits, cleanly or by crashing.
///     <para>This is the test that covers the CRASH path without crashing a test host. The
///     app's clean-exit path is covered separately (disposal runs the explicit kill); this
///     one proves the backstop that catches a kill, a fault, or a Task-Manager end.</para>
///     <para>The seam is reached by reflection because this project does not reference the
///     Windows ACL assembly: the alternative is a project reference for one P/Invoke
///     wrapper, which is the wrong trade.</para></summary>
[Trait("Category", "Process")]
public sealed class HostContainmentTests
{
  private static readonly Type JobObject = Type.GetType("eThangAgent.Windows.ACL.JobObject, eThangAgent.Windows.ACL", throwOnError: true)!;

  private static nint Create() => (nint)JobObject
      .GetMethod("Create", BindingFlags.Public | BindingFlags.Static)!
      .Invoke(null, null)!;

  private static bool Configure(nint job) => (bool)JobObject
      .GetMethod("Configure", BindingFlags.Public | BindingFlags.Static)!
      .Invoke(null, [job])!;

  private static bool Attach(nint job, Process process) => (bool)JobObject
      .GetMethod("Attach", BindingFlags.Public | BindingFlags.Static)!
      .Invoke(null, [job, process])!;

  private static bool Close(nint job) => (bool)JobObject
      .GetMethod("Close", BindingFlags.Public | BindingFlags.Static)!
      .Invoke(null, [job])!;

  /// <summary>A long-lived child to contain: the test host's own runtime runs a child
  ///     process, so containment is proven against a REAL process, not a mock.</summary>
  private static Process StartLongLivedChild()
  {
    ProcessStartInfo psi = new("cmd.exe", "/c ping -n 120 127.0.0.1 > nul")
    {
      UseShellExecute = false,
      CreateNoWindow = true,
    };
    return Process.Start(psi) ?? throw new InvalidOperationException("could not start the child process");
  }

  /// <summary>Closing ONLY the job handle must reap the process. The explicit kill is never
  ///     called, so a pass proves the JOB did the work - the same path the kernel takes when
  ///     the app process dies holding the handle.</summary>
  [Fact]
  public async Task ClosingTheJob_ReapsTheContainedProcess()
  {
    using Process child = StartLongLivedChild();
    try
    {
      nint job = Create();
      Assert.NotEqual(nint.Zero, job);
      Assert.True(Configure(job), "the job must accept the kill-on-close limit");
      Assert.True(Attach(job, child), "the child must join the job");

      // The child is alive and contained; closing the handle is the ONLY action taken.
      Assert.False(child.HasExited, "the child must still be running before the job closes");
      _ = Close(job);

      Task exit = child.WaitForExitAsync(TestContext.Current.CancellationToken);
      bool reaped = await Task.WhenAny(exit, Task.Delay(TimeSpan.FromSeconds(10),
          TestContext.Current.CancellationToken)).ConfigureAwait(true) == exit;
      Assert.True(reaped || child.HasExited,
          "closing a kill-on-close job must reap the process it contains");
    }
    finally
    {
      if (!child.HasExited)
      {
        child.Kill(entireProcessTree: true);
      }
    }
  }

  /// <summary>A process NOT in the job is untouched by closing it: containment is exact,
  ///     never a blanket kill of whatever happens to be running.</summary>
  [Fact]
  public async Task ClosingTheJob_LeavesAnUncontainedProcessRunning()
  {
    using Process contained = StartLongLivedChild();
    using Process bystander = StartLongLivedChild();
    try
    {
      nint job = Create();
      Assert.NotEqual(nint.Zero, job);
      Assert.True(Configure(job));
      Assert.True(Attach(job, contained));

      _ = Close(job);
      await Task.Delay(TimeSpan.FromMilliseconds(500), TestContext.Current.CancellationToken).ConfigureAwait(true);

      Assert.False(bystander.HasExited,
          "a process outside the job must survive it - containment must be exact");
    }
    finally
    {
      foreach (Process p in new[] { contained, bystander })
      {
        if (!p.HasExited)
        {
          p.Kill(entireProcessTree: true);
        }
      }
    }
  }
}
