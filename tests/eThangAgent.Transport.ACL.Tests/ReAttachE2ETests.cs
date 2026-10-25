using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using eThangAgent.Composition;

namespace eThangAgent.Transport.ACL.Tests;

/// <summary>CONTAINMENT, not re-attach (issue #274). A REAL ChildHost process serves ONE
///     app connection and then exits: the app contains it in a kill-on-close Job Object, so
///     the host dies with the app - clean exit or crash alike - and there is no later app to
///     re-attach. This test pins the host half of that contract against the real executable:
///     when the app's connection drops, the host process EXITS instead of waiting for a
///     second client.</summary>
public class ReAttachE2ETests
{
  private static string FindHostExe()
  {
    // The shared resolution: the repo's live build output in a checkout, else the
    // copy shipped next to the app. A missing repo is an ordinary case, not a fault
    // (the 2026-10-21 crash walked past the filesystem root and dereferenced null).
    return ChildHostExeLocator.Resolve(
        ChildHostExeLocator.FindRepoRoot(typeof(ReAttachE2ETests).Assembly.Location),
        AppContext.BaseDirectory);
  }

  /// <summary>The retired behavior, inverted: the host used to accept loop across a dropped
  ///     connection so a relaunching app could adopt the children it kept running. Now the
  ///     process exits, which is what makes 'closing the app leaves nothing running' true at
  ///     the process level rather than only at the job-object level.</summary>
  [Fact]
  public async Task HostConnectionDrop_ExitsTheProcess_InsteadOfWaitingForReAttach()
  {
    string hostExe = FindHostExe();
    Assert.True(File.Exists(hostExe), "child host exe not built");

    string pipeName = "ethang-containment-" + Guid.NewGuid().ToString("N");
    string scratch = Path.Combine(Path.GetTempPath(), "e2e-" + Guid.NewGuid().ToString("N"));
    _ = Directory.CreateDirectory(scratch);
    await File.WriteAllTextAsync(Path.Combine(scratch, "settings.json"), /*lang=json,strict*/ "{\"OpenRouter\":{\"ApiKey\":null},\"Zai\":{\"ApiKey\":null}}",
        TestContext.Current.CancellationToken).ConfigureAwait(true);

    ProcessStartInfo psi = new(hostExe);
    psi.ArgumentList.Add(pipeName);
    psi.ArgumentList.Add(Path.Combine(scratch, "settings.json"));
    psi.ArgumentList.Add(Path.Combine(scratch, "db.sqlite"));
    psi.RedirectStandardOutput = true;
    psi.RedirectStandardError = true;
    psi.UseShellExecute = false;

    using Process host = Process.Start(psi) ?? throw new InvalidOperationException("child host failed to start.");
    // A kill-on-close job, exactly as the supervisor does it in production: if this TEST
    // process dies the host goes with it, so a failed assertion can never strand a host
    // (the assembly leak guard would report it, but not reaping it is the real fix).
    nint job = CreateJob();
    if (job != nint.Zero)
    {
      _ = AttachJob(job, host);
    }

    ConcurrentQueue<string> hostLog = new();
    _ = Task.Run(async () =>
    {
      while (await host.StandardOutput.ReadLineAsync(TestContext.Current.CancellationToken).ConfigureAwait(true) is { } line)
      {
        hostLog.Enqueue(line);
      }
    }, TestContext.Current.CancellationToken);
    _ = Task.Run(async () =>
    {
      while (await host.StandardError.ReadLineAsync(TestContext.Current.CancellationToken).ConfigureAwait(true) is { } line)
      {
        hostLog.Enqueue("ERR: " + line);
      }
    }, TestContext.Current.CancellationToken);
    try
    {
      // A handshake blip under load is a STARTUP race, not the contract under test: the
      // host's single accept can be disturbed by another test's pipe traffic on a loaded
      // machine, and the app-side answer to that is to relaunch (which the supervisor does).
      // The contract asserted below is what happens AFTER a connection is established.
      NamedPipeChildTransport app = await ConnectAsync(pipeName, hostLog).ConfigureAwait(true);
      RemoteAgentRuntime runtime = new(app);
      using CancellationTokenSource loop = new();
      _ = runtime.RunReceiveLoopAsync(loop.Token);

      // The connection drops - what an app exit looks like from the host's side.
      await app.DisposeAsync().ConfigureAwait(true);

      // The host must EXIT. Under the retired accept loop it would have stayed alive
      // waiting for a second client, which is exactly the orphan this issue reports.
      Task exit = host.WaitForExitAsync(TestContext.Current.CancellationToken);
      bool exited = await Task.WhenAny(exit, Task.Delay(TimeSpan.FromSeconds(15),
          TestContext.Current.CancellationToken)).ConfigureAwait(true) == exit;
      Assert.True(exited,
          "the host must exit when its only app connection drops. host log: " + string.Join(" | ", hostLog));
      Assert.Contains(hostLog, l => l.Contains("app-disconnected", StringComparison.Ordinal));
    }
    finally
    {
      if (!host.HasExited)
      {
        host.Kill(entireProcessTree: true);
      }

      if (job != nint.Zero)
      {
        _ = CloseJob(job);
      }

      try
      {
        Directory.Delete(scratch, recursive: true);
      }
      catch (IOException)
      {
        // best effort: a still-draining host may pin the directory
      }
    }
  }

  /// <summary>Connects to the host. A handshake blip under load is a STARTUP race, not the
  ///     contract under test (the host's single accept can be disturbed by another test's pipe
  ///     traffic on a loaded machine), so a failed connect names the host's own log rather than
  ///     reporting a bare timeout - the assertion that follows is about what a DROPPED
  ///     connection does, and a startup failure must never be mistaken for that.</summary>
  private static async Task<NamedPipeChildTransport> ConnectAsync(
      string pipeName, ConcurrentQueue<string> hostLog)
  {
    try
    {
      return await NamedPipeChildTransport.ConnectToHostAsync(pipeName,
          TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(15),
              TestContext.Current.CancellationToken).ConfigureAwait(true);
    }
    catch (Exception ex) when (ex is IOException or TimeoutException or OperationCanceledException)
    {
      throw new InvalidOperationException(
          "the host never accepted its connection. host log: " + string.Join(" | ", hostLog), ex);
    }
  }

  /// <summary>The composition's kill-on-close job, reached by reflection (internal type).</summary>
  private static nint CreateJob() => (nint)JobObjectType
      .GetMethod("Create", BindingFlags.Public | BindingFlags.Static)!.Invoke(null, null)!;

  private static bool AttachJob(nint job, Process process) => (bool)JobObjectType
      .GetMethod("Attach", BindingFlags.Public | BindingFlags.Static)!.Invoke(null, [job, process])!;

  private static bool CloseJob(nint job) => (bool)JobObjectType
      .GetMethod("Close", BindingFlags.Public | BindingFlags.Static)!.Invoke(null, [job])!;

  private static readonly Type JobObjectType = typeof(ChildHostExeLocator).Assembly
      .GetType("eThangAgent.Composition.JobObject", throwOnError: true)!;
}
