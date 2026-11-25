using System.Text.Json;

namespace eThangAgent.ComputerUse.Host.Tests;

/// <summary>A handler fault answers a typed error on the wire. It must never escape the
///     dispatch boundary: an exception out of a connection task disposes that connection's
///     pipe, the client reads EOF as transport loss, and the supervisor answers by killing
///     and respawning the broker - so one faulty handler becomes a dropped connection and a
///     wasted restart rather than a result the caller can act on.</summary>
public class DispatchFaultContainmentTests
{
  private sealed class ThrowingObserver : IBrokerObserver
  {
    public JsonElement? ListApplications() => null;

    public JsonElement? ListWindows(JsonElement? parameters) => null;

    public JsonElement? CaptureApp(JsonElement? parameters) =>
        throw new InvalidOperationException("observer exploded");

    public void OnOwnerLost(int ownerConnectionId) { }
  }

  private static PipeServer ServerWith(IBrokerObserver observer) =>
      new(new BrokerConfig("ignored-pipe", "t"), observer,
          foregroundPid: () => -1, hooks: FakeConnectionFactory.InertHooks());

  [Fact]
  public void ObserverFault_AnswersTypedError_InsteadOfThrowing()
  {
    PipeServer server = ServerWith(new ThrowingObserver());
    _ = server.Dispatch(0, "authenticate", JsonElement.Parse("""{"token":"t"}"""), 1);

    BrokerResponse reply = server.Dispatch(2, "capture_app", JsonElement.Parse("""{}"""), 1);

    _ = Assert.NotNull(reply.Error);
    Assert.Equal("internal", reply.Error.Value.Code);
    Assert.Contains("observer exploded", reply.Error.Value.Message, StringComparison.Ordinal);
  }

  [Fact]
  public void ObserverFault_KeepsConnectionUsable_ForTheNextRequest()
  {
    PipeServer server = ServerWith(new ThrowingObserver());
    _ = server.Dispatch(0, "authenticate", JsonElement.Parse("""{"token":"t"}"""), 1);

    _ = server.Dispatch(2, "capture_app", JsonElement.Parse("""{}"""), 1);
    BrokerResponse after = server.Dispatch(3, "no_such_method", null, 1);

    _ = Assert.NotNull(after.Error);
    Assert.Equal("method_not_found", after.Error.Value.Code);
  }
}
