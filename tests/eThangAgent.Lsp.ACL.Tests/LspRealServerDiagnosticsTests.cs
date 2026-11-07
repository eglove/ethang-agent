using eThangAgent.Lsp.ACL;
using eThangAgent.TestSupport;
using eThangAgent.ToolDomain.Lsp;

namespace eThangAgent.Lsp.ACL.Tests;

/// <summary>Drives the REAL bundled Roslyn server through the production daemon
/// executable and the production pipe protocol, so the path an edit takes is exercised
/// end to end: connect cold, ask for diagnostics, announce a changed file, ask again.
/// Opt-in by trait - it needs a built daemon and a machine where Roslyn can load the
/// workspace - and out of the default suite. Run it with
/// --filter-trait "Category=OptIn" with ETHANG_LSP_REAL_SERVER=1.</summary>
[Trait("Category", "OptIn")]
[Trait("Category", "RealLspServer")]
public class LspRealServerDiagnosticsTests
{
  private static CancellationToken Ct => TestContext.Current.CancellationToken;

  [Fact]
  public async Task AdvisoryDiagnostics_ColdQueryThenAnnouncement_ReturnsTheTypeError()
  {
    OptInTest.SkipUnlessEnabled("ETHANG_LSP_REAL_SERVER",
        "with a built daemon and a resolvable Roslyn server.");

    string daemonExe = LspDaemonExeLocator.CreateDefault().Resolve();
    Assert.True(File.Exists(daemonExe), "the language-server daemon must be built first: " + daemonExe);

    string root = TestTempRoot.CreateDirectory("ethang-lsp-real").FullName;
    string workspaceId = "real-" + Guid.NewGuid().ToString("N");
    string pipeName = LspPipeNames.ForWorkspaceId(workspaceId);
    try
    {
      await File.WriteAllTextAsync(Path.Combine(root, "probe.csproj"),
          "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>", Ct).ConfigureAwait(true);
      await File.WriteAllTextAsync(Path.Combine(root, "probe.slnx"),
          "<Solution>\n  <Project Path=\"probe.csproj\" />\n</Solution>\n", Ct).ConfigureAwait(true);
      string file = Path.Combine(root, "Bad.cs");
      await File.WriteAllTextAsync(file, "public static class Bad\n{\n  public static int Value() => 1;\n}\n", Ct).ConfigureAwait(true);

      System.Diagnostics.ProcessStartInfo psi = new(daemonExe)
      {
        CreateNoWindow = true,
        UseShellExecute = false,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
      };
      psi.ArgumentList.Add(pipeName);
      psi.ArgumentList.Add(workspaceId);
      psi.ArgumentList.Add(root);
      using System.Diagnostics.Process daemon = System.Diagnostics.Process.Start(psi)!;
      try
      {
        _ = daemon.StandardOutput.ReadToEndAsync(Ct);

        // The FIRST query is what an edit on a cold session makes: the daemon does not
        // exist yet, so this is the connect-and-ask path, not a warm one.
        LspPipeClient client = await ConnectAsync(pipeName, workspaceId).ConfigureAwait(true);
        AdvisoryDiagnosticsResult cold = await client.GetDiagnosticsAsync(file, Ct).ConfigureAwait(true);
        Assert.NotEqual(AdvisoryDiagnosticsState.Unavailable, cold.State);

        await File.WriteAllTextAsync(
            file, "public static class Bad\n{\n  public static int Value() => \"not an int\";\n}\n", Ct).ConfigureAwait(true);
        await client.SendWatcherAnnouncementAsync(
            new WatcherAnnouncement(workspaceId, ["Bad.cs"], [], 1, DateTimeOffset.UtcNow), Ct).ConfigureAwait(true);

        AdvisoryDiagnosticsResult result = await PollUntilReportedAsync(client, file).ConfigureAwait(true);
        Assert.Equal(AdvisoryDiagnosticsState.Ready, result.State);
        AdvisoryDiagnostic error = Assert.Single(result.Diagnostics, d => d.Code == "CS0029");
        Assert.Equal(3, error.StartLine);
        Assert.Contains("Cannot implicitly convert type", error.Message, StringComparison.Ordinal);

        // A second query after the announcement must still carry the diagnostic: an
        // edit-time query is not watcher-driven and is never labeled outdated.
        AdvisoryDiagnosticsResult again = await client.GetDiagnosticsAsync(file, Ct).ConfigureAwait(true);
        Assert.Equal(AdvisoryDiagnosticsState.Ready, again.State);
        Assert.Contains(again.Diagnostics, d => d.Code == "CS0029");
        client.Dispose();
      }
      finally
      {
        if (!daemon.HasExited)
        {
          daemon.Kill(entireProcessTree: true);
        }
      }
    }
    finally
    {
      DeleteWorkspace(root);
    }
  }

  /// <summary>Connects to the daemon the test started, waiting out its startup - the
  ///     daemon resolves and possibly downloads the server before it listens.</summary>
  private static async Task<LspPipeClient> ConnectAsync(string pipeName, string workspaceId)
  {
    for (int attempt = 0; attempt < 600; attempt++)
    {
      try
      {
        return await LspPipeClient.ConnectAsync(pipeName, workspaceId, "app-1", Ct).ConfigureAwait(true);
      }
      catch (Exception ex) when (ex is TimeoutException or IOException)
      {
        await Task.Delay(250, Ct).ConfigureAwait(true);
      }
    }

    throw new InvalidOperationException("the language-server daemon never accepted a connection");
  }

  /// <summary>Polls until the server reports for the file. Roslyn answers an empty item
  ///     list until its compilation for the changed document is rebuilt, so a single
  ///     query right after the announcement can legitimately be empty.</summary>
  private static async Task<AdvisoryDiagnosticsResult> PollUntilReportedAsync(LspPipeClient client, string file)
  {
    AdvisoryDiagnosticsResult result = new(AdvisoryDiagnosticsState.Starting, []);
    System.Diagnostics.Stopwatch elapsed = System.Diagnostics.Stopwatch.StartNew();
    while (elapsed.Elapsed < TestCeiling.Value)
    {
      result = await client.GetDiagnosticsAsync(file, Ct).ConfigureAwait(true);
      if (result.Diagnostics.Count > 0)
      {
        return result;
      }

      await Task.Delay(1000, Ct).ConfigureAwait(true);
    }

    return result;
  }

  private static void DeleteWorkspace(string root)
  {
    for (int attempt = 0; attempt < 20; attempt++)
    {
      try
      {
        Directory.Delete(root, recursive: true);
        return;
      }
      catch (IOException)
      {
        Thread.Sleep(50);
      }
      catch (UnauthorizedAccessException)
      {
        Thread.Sleep(50);
      }
    }
  }
}
