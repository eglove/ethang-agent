using System.Diagnostics;
using eThangAgent.ToolDomain;

namespace eThangAgent.ComputerUse.ACL.Tests;

/// <summary>Fix round 4 (finding 4 residual, binding ruling): NO raw exception may
///     escape the connect path. The committed crash class - JobObject.Attach's
///     InvalidOperationException on an inert Process - escaped RequestEnvelopeAsync
///     raw (its catch list covers only typed broker failures) and left the faulted
///     _connecting cached forever. The spawn delegate is the same seam, so these tests
///     model the crash by throwing the SAME exception type from it.</summary>
public class RawExceptionWrapTests
{
  [Fact]
  public async Task SpawnDelegateRawException_WrapsTyped_NeverEscapesRaw()
  {
    string pipeName = "ethang-raw-" + Guid.NewGuid().ToString("N");
    BrokerSupervisor supervisor = new(@"C:\no\such\host.exe", pipeName, "raw-ws",
      spawn: (_, _, _) => throw new InvalidOperationException("No process is associated with this object."));
    try
    {
      BrokerEnvelopeException typed = await Assert.ThrowsAsync<BrokerEnvelopeException>(
        () => supervisor.RequestEnvelopeAsync("list_applications", null, TestContext.Current.CancellationToken)).ConfigureAwait(true);
      Assert.Equal(ComputerErrorCodes.HelperUnavailable, typed.Code);
      InvalidOperationException inner = Assert.IsType<InvalidOperationException>(typed.InnerException);
      Assert.Contains("associated", inner.Message, StringComparison.Ordinal);
    }
    finally
    {
      await supervisor.DisposeAsync().ConfigureAwait(true);
    }
  }

  [Fact]
  public async Task SpawnDelegateRawException_DoesNotStayCached_HealsOnNextCall()
  {
    string pipeName = "ethang-heal2-" + Guid.NewGuid().ToString("N");
    string hostPath = StubHostBuilder.Build();
    bool poisoned = true;
    BrokerSupervisor supervisor = new(hostPath, pipeName, "heal-ws",
      spawn: (exe, pipe, token) => poisoned
        ? throw new InvalidOperationException("No process is associated with this object.")
        : SpawnForTest(exe, pipe, token));
    try
    {
      BrokerEnvelopeException first = await Assert.ThrowsAsync<BrokerEnvelopeException>(
        () => supervisor.RequestEnvelopeAsync("list_applications", null, TestContext.Current.CancellationToken)).ConfigureAwait(true);
      Assert.Equal(ComputerErrorCodes.HelperUnavailable, first.Code);

      // The fault must not stay cached: once the seam heals, the SAME supervisor
      // instance retries the spawn and serves, instead of surfacing the stale fault.
      poisoned = false;
      BrokerReply healed = await supervisor.RequestEnvelopeAsync("list_applications", null, TestContext.Current.CancellationToken);
      Assert.Null(healed.Error);
      Assert.True(healed.Result is not null, "the healed spawn must serve the request");
    }
    finally
    {
      await supervisor.DisposeAsync().ConfigureAwait(true);
    }
  }

  private static Process SpawnForTest(string exePath, string pipeName, string token)
  {
    ProcessStartInfo psi = new(exePath, pipeName)
    {
      UseShellExecute = false,
      CreateNoWindow = true,
      EnvironmentVariables = { ["ETHANG_COMPUTER_USE_TOKEN"] = token },
    };
    return Process.Start(psi)!;
  }
}
