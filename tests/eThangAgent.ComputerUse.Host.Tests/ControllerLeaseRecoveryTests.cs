using System.Text.Json;

namespace eThangAgent.ComputerUse.Host.Tests;

/// <summary>Three lease properties: an unowned lease never refuses input; every refusal
///     names the owner the same way stop does; and every transition is logged with its
///     owner.</summary>
public class ControllerLeaseRecoveryTests
{
  private static JsonElement KeyParams => JsonElement.Parse("{\"key\":\"a\"}");

  [Fact]
  public void InputMethod_OnFreeLease_TakesTheLease_InsteadOfRefusing()
  {
    PipeServer server = FakeConnectionFactory.Authorized(4);
    BrokerResponse reply = server.Dispatch(2, "press_key", KeyParams, connectionId: 4);
    Assert.Null(reply.Error);
    Assert.Equal(4, server.Lease.Owner);
  }

  [Fact]
  public void InputRefusal_AndStopRefusal_NameTheSameOwner()
  {
    PipeServer server = FakeConnectionFactory.Authorized(1, 2);
    _ = server.Dispatch(2, "controller_takeover", null, connectionId: 1);

    BrokerResponse input = server.Dispatch(3, "press_key", KeyParams, connectionId: 2);
    BrokerResponse stop = server.Dispatch(4, "stop_computer_control", null, connectionId: 2);

    _ = Assert.NotNull(input.Error);
    _ = Assert.NotNull(stop.Error);
    Assert.Equal("owner=1", input.Error.Value.Details);
    Assert.Contains("owner=1", stop.Error.Value.Message, StringComparison.Ordinal);
    Assert.DoesNotContain("owner=0", input.Error.Value.Message, StringComparison.Ordinal);
  }

  [Fact]
  public void LeaseTransitions_AreLogged_WithTheOwnerId()
  {
    List<string> lines = [];
    PipeServer server = new(new BrokerConfig("ignored-pipe", "t"), log: lines.Add);
    _ = server.Dispatch(0, "authenticate", JsonElement.Parse("{\"token\":\"t\"}"), connectionId: 1);
    _ = server.Dispatch(0, "authenticate", JsonElement.Parse("{\"token\":\"t\"}"), connectionId: 2);

    _ = server.Dispatch(2, "controller_takeover", null, connectionId: 1);
    _ = server.Dispatch(3, "controller_takeover", null, connectionId: 2);
    _ = server.Dispatch(4, "controller_stop", null, connectionId: 1);

    Assert.Contains(lines, line => line.Contains("lease taken: owner=1", StringComparison.Ordinal));
    Assert.Contains(lines, line => line.Contains("lease refused: owner=1", StringComparison.Ordinal));
    Assert.Contains(lines, line => line.Contains("lease released: owner=1", StringComparison.Ordinal));
  }
};
