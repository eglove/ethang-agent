using eThangAgent.ChildHost;
using eThangAgent.SharedKernel;

namespace eThangAgent.Transport.ACL.Tests;

/// <summary>The conversation host is a SIDECAR APP: it ships next to the app and runs as
///     its own process, so it needs its own dependency manifest
///     (eThangAgent.ChildHost.deps.json) to resolve its NATIVE assets - e_sqlite3.dll
///     for SQLite. A published app copies a referenced project's assemblies but NOT its
///     deps.json, so a published host could not load SQLite at all: every turn faulted
///     with "The type initializer for 'Microsoft.Data.Sqlite.SqliteConnection' threw an
///     exception", and before the root-cause reporting it read as "this conversation
///     host is not bound to a root session" (2026-10-22).
///     <para>These tests pin the two halves: a host whose deployment is incomplete
///     SAYS SO (with the root cause), and the fault formatter never hides a cause
///     behind a wrapper exception.</para></summary>
public class ChildHostDeploymentTests
{
  private static readonly TimeSpan Limit = TimeSpan.FromSeconds(30);

  [Fact]
  public async Task AHostWithoutItsDependencyManifest_NamesTheDeploymentFault()
  {
    // A staged copy of this assembly's output WITHOUT the host's manifest: the same
    // shape a published app had. The host must name the deployment gap, not fail with
    // an opaque type-initializer error.
    string staged = Path.Combine(Path.GetTempPath(), "ethang-broken-deploy-" + Guid.NewGuid().ToString("N"));
    _ = Directory.CreateDirectory(staged);
    try
    {
      CopyOutput(AppContext.BaseDirectory, staged);
      string manifest = Path.Combine(staged, "eThangAgent.ChildHost.deps.json");
      Assert.True(File.Exists(manifest), "the staged output should carry the host's manifest before it is removed.");
      File.Delete(manifest);

      (Result<string> result, List<RootTurnEvent> events) =
          await RunRootTurnAsync(Path.Combine(staged, "eThangAgent.ChildHost.exe")).ConfigureAwait(true);

      Assert.True(result.IsSuccess, result.Error?.Message);
      RootTurnEvent settle = Assert.Single(events);
      Assert.Equal("settled", settle.Kind);
      Assert.Contains("RootTurnUnavailable", settle.Payload, StringComparison.Ordinal);
      // The REAL cause, named: the native library the deployment could not provide.
      Assert.True(
          settle.Payload.Contains("could not load its SQLite native library", StringComparison.Ordinal),
          "PAYLOAD: " + settle.Payload);
      // And the root cause under it (the wrapper's own message hides the DLL).
      Assert.True(
          settle.Payload.Contains("e_sqlite3", StringComparison.Ordinal),
          "PAYLOAD: " + settle.Payload);
    }
    finally
    {
      // Named decision (CA1031): staging cleanup is best effort - the assertion that
      // failed above is the finding, not this cleanup.
#pragma warning disable CA1031
      try
      {
        Directory.Delete(staged, recursive: true);
      }
      catch (Exception)
      {
        // best effort
      }
#pragma warning restore CA1031
    }
  }

  [Fact]
  public void HostFault_CarriesTheContextAndTheRootCause()
  {
    // The wrapper's message says only that some static initializer threw; the reader
    // needs the innermost cause - AND the context that makes it actionable.
    InvalidOperationException wrapper = new("outer context", new DllNotFoundException("Unable to load DLL 'e_sqlite3'."));

    string described = HostFault.Describe(wrapper);

    Assert.Contains("outer context", described, StringComparison.Ordinal);
    Assert.Contains("DllNotFoundException", described, StringComparison.Ordinal);
    Assert.Contains("e_sqlite3", described, StringComparison.Ordinal);
  }

  [Fact]
  public void HostFault_WithoutAnInnerException_KeepsTheMessage()
      => Assert.Equal("boom", HostFault.Describe(new InvalidOperationException("boom")));

  /// <summary>Runs one root turn against a staged host executable and returns the settle
  ///     plus the events it produced.</summary>
  private static async Task<(Result<string> Result, List<RootTurnEvent> Events)> RunRootTurnAsync(string exe)
  {
    string settings = Path.Combine(Path.GetTempPath(), "ethang-deploy-" + Guid.NewGuid().ToString("N") + ".json");
    await File.WriteAllTextAsync(settings,
        /*lang=json,strict*/ "{\"OpenRouter\":{\"ApiKey\":\"sk-or-test\",\"BaseUrl\":\"http://openrouter.test\"},\"SubAgents\":{}}",
        TestContext.Current.CancellationToken).ConfigureAwait(true);
    string pipe = "ethang-deploy-" + Guid.NewGuid().ToString("N");
    System.Diagnostics.ProcessStartInfo psi = new(exe);
    psi.ArgumentList.Add(pipe);
    psi.ArgumentList.Add(settings);
    psi.ArgumentList.Add(Path.Combine(Path.GetTempPath(), "ethang-deploy-" + Guid.NewGuid().ToString("N") + ".db"));
    psi.RedirectStandardOutput = true;
    psi.RedirectStandardError = true;
    psi.UseShellExecute = false;
    System.Diagnostics.Process? host = System.Diagnostics.Process.Start(psi);
    try
    {
      // Named decision (CA2007): 'await using' cannot carry ConfigureAwait.
#pragma warning disable CA2007
      await using NamedPipeChildTransport app = await NamedPipeChildTransport
          .ConnectToHostAsync(pipe, TestContext.Current.CancellationToken)
          .WaitAsync(Limit, TestContext.Current.CancellationToken)
          .ConfigureAwait(true);
#pragma warning restore CA2007
      RemoteAgentRuntime runtime = new(app);
      using CancellationTokenSource loop = new();
      _ = runtime.RunReceiveLoopAsync(loop.Token);
      List<RootTurnEvent> events = [];
      Result<string> result = await runtime.SendRootTurnAsync(
          new RootTurnRequest("turn-deploy", "hello", []), events.Add,
          TestContext.Current.CancellationToken).WaitAsync(Limit, TestContext.Current.CancellationToken).ConfigureAwait(true);
      return (result, events);
    }
    finally
    {
      // Named decision (CA1031): killing a staged host is best effort.
#pragma warning disable CA1031
      try
      {
        if (host is not null && !host.HasExited)
        {
          host.Kill(entireProcessTree: true);
        }
      }
      catch (Exception)
      {
        // best effort
      }
#pragma warning restore CA1031
    }
  }

  /// <summary>Copies this assembly's output folder, keeping only the win-x64 native
  ///     assets (the other RIDs are megabytes of noise for a Windows-only test).</summary>
  private static void CopyOutput(string source, string destination)
  {
    foreach (string file in Directory.EnumerateFiles(source))
    {
      File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), overwrite: true);
    }

    foreach (string directory in Directory.EnumerateDirectories(source))
    {
      string name = Path.GetFileName(directory);
      if (string.Equals(name, "runtimes", StringComparison.Ordinal))
      {
        string rid = Path.Combine(directory, "win-x64");
        if (Directory.Exists(rid))
        {
          CopyDirectory(rid, Path.Combine(destination, "runtimes", "win-x64"));
        }

        continue;
      }

      CopyDirectory(directory, Path.Combine(destination, name));
    }
  }

  private static void CopyDirectory(string source, string destination)
  {
    _ = Directory.CreateDirectory(destination);
    foreach (string file in Directory.EnumerateFiles(source))
    {
      File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), overwrite: true);
    }

    foreach (string directory in Directory.EnumerateDirectories(source))
    {
      CopyDirectory(directory, Path.Combine(destination, Path.GetFileName(directory)));
    }
  }
}
