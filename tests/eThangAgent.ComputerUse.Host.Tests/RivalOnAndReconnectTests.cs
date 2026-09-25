using eThangAgent.ComputerUse.ACL;
using eThangAgent.ToolDomain;

namespace eThangAgent.ComputerUse.Host.Tests;

/// <summary>D/E (controller rulings, wire-level, REAL broker from HostHarness): RivalOn
///     must yield a PLAIN AUTHENTICATED connection on the existing broker's pipe -
///     authenticate + hello, no second supervisor, no second process - and a failed
///     spawn/connect must clear the cached connecting task so the NEXT call retries
///     instead of surfacing the cached fault forever.</summary>
public class RivalOnAndReconnectTests
{
  [Fact]
  public async Task RivalOn_ConnectsAuthenticated_NoSecondProcess()
  {
    string pipeName = "ethang-cu-host-test-" + Guid.NewGuid().ToString("N");
    await using HostHarness harness = HostHarness.Start(pipeName);
    string existingFile = Path.Combine(AppContext.BaseDirectory, "eThangAgent.ComputerUse.Host.Tests.dll");

    BrokerSupervisor primary = new(existingFile, pipeName, "rival-ws",
      spawn: (_, _, _) => throw new InvalidOperationException("a rival must never spawn a process"));

    // The harness broker minted its own token; the rival must present THAT token.
    NdjsonPipeClient rival = await BrokerSupervisor.RivalOn(primary, harness.Token, TestContext.Current.CancellationToken).ConfigureAwait(true);
    try
    {
      BrokerReply reply = await rival.RequestAsync("list_applications", null, TestContext.Current.CancellationToken).ConfigureAwait(true);
      Assert.Null(reply.Error);
      _ = Assert.NotNull(reply.Result);
    }
    finally
    {
      await rival.DisposeAsync().ConfigureAwait(true);
      await primary.DisposeAsync().ConfigureAwait(true);
    }
  }

  [Fact]
  public async Task FaultedConnect_IsCleared_NextCallRetries_NotTheCachedFault()
  {
    string pipeName = "ethang-cu-heal-" + Guid.NewGuid().ToString("N");

    // First call: a spawn path that does not exist - the connect attempt fails.
    string missing = Path.Combine(Path.GetTempPath(), "no-such-host-" + Guid.NewGuid().ToString("N") + ".exe");
    string realHostPath = HealedHost.RealPath();
    bool healed = false;
    BrokerSupervisor supervisor = new(missing, pipeName, "heal-ws",
      hostPathForSpawn: resolved => healed ? realHostPath : resolved);
    try
    {
      BrokerEnvelopeException first = await Assert.ThrowsAsync<BrokerEnvelopeException>(
        () => supervisor.RequestEnvelopeAsync("list_applications", null, TestContext.Current.CancellationToken)).ConfigureAwait(true);
      Assert.Equal(ComputerErrorCodes.HelperUnavailable, first.Code);
      Assert.Contains("host", first.Message, StringComparison.Ordinal);

      // The switch to a REAL host path models the transient fault healing. The pre-fix
      // bug: the faulted _connecting task stayed cached, so this call surfaced the SAME
      // host-not-found fault no matter what. Post-fix: the next call spawns+connects fresh.
      healed = true;
      BrokerReply reply = await supervisor.RequestEnvelopeAsync("list_applications", null, TestContext.Current.CancellationToken).ConfigureAwait(true);
      Assert.Null(reply.Error);
      _ = Assert.NotNull(reply.Result);
    }
    finally
    {
      await supervisor.DisposeAsync().ConfigureAwait(true);
    }
  }
}

/// <summary>The REAL broker exe this worktree builds - the healed spawn path.
///     Resolves the test run's own configuration (parsed from the test assembly
///     path) with any existing build as fallback: CI builds/tests Release only,
///     local runs use Debug — hard-coding either broke the other side.</summary>
internal static class HealedHost
{
  public static string RealPath()
  {
    string binRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
        "..", "..", "..", "..", "..", "src", "eThangAgent.ComputerUse.Host", "bin"));
    // The test assembly lives at …\bin\{config}\net10.0-windows\; walk up one
    // level and take the configuration directory's name.
    string? binConfigDir = Path.GetDirectoryName(Path.GetDirectoryName(
        Path.GetFullPath(typeof(RivalOnAndReconnectTests).Assembly.Location)));
    string? configuration = Path.GetFileName(binConfigDir);
    if (configuration is "Debug" or "Release")
    {
      string candidate = Path.Combine(binRoot, configuration, "net10.0-windows", "eThangAgent.ComputerUse.Host.exe");
      if (File.Exists(candidate))
      {
        return candidate;
      }
    }

    string? newest = Directory.EnumerateFiles(binRoot, "eThangAgent.ComputerUse.Host.exe", SearchOption.AllDirectories)
        .OrderByDescending(File.GetLastWriteTimeUtc)
        .FirstOrDefault();
    return newest ?? Path.Combine(binRoot, "Debug", "net10.0-windows", "eThangAgent.ComputerUse.Host.exe");
  }
}
