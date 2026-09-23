using System.Text.Json;

namespace eThangAgent.ComputerUse.Host.Tests;

/// <summary>Fix round I4: the broker's input layer must subscribe to Lease.OwnerLost:
///     cancel active holds and clear synthetic modifiers when the owner is lost. The send
///     hook is a recording double (always succeeds) so the hold IS active deterministically;
///     production wires the real SendInput with the same contract.</summary>
public class A3InputSubscriptionTests
{
  [Fact]
  public void OwnerLost_CancelsActiveHold_AndClearsModifiers()
  {
    PipeServer server = FakeConnectionFactory.WithAlwaysSucceedingInput();
    _ = server.Dispatch(0, "authenticate", JsonDocument.Parse("""{"token":"t"}""").RootElement, connectionId: 1);
    _ = server.Dispatch(2, "controller_takeover", null, connectionId: 1);
    _ = server.Dispatch(3, "hold_key", JsonDocument.Parse("""{"key":"ctrl+a","hold_seconds":5}""").RootElement, connectionId: 1);
    Assert.True(server.InputDispatch.HasActiveHold, "a hold must be ACTIVE after hold_key");
    Assert.True(server.InputDispatch.HasSyntheticModifiers, "modifiers are synthetic during a hold");
    server.DropConnection(1);
    Assert.False(server.InputDispatch.HasActiveHold, "A3: owner loss must cancel the hold");
    Assert.False(server.InputDispatch.HasSyntheticModifiers, "A3: owner loss must clear synthetic modifiers");
  }
};
