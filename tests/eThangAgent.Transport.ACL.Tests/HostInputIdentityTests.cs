namespace eThangAgent.Transport.ACL.Tests;

/// <summary>The root turn request carries the IDENTITY of the user message it delivers.
///     <para>WHY IT IS ON THE WIRE: a host that dies mid-turn is reconnected and the turn is
///     resent, so the replacement host must be able to tell 'this input already arrived'
///     from 'this input is new'. Without an identity on the request it can only compare
///     TEXT, which would silently drop a sentence the user legitimately typed twice.</para>
///     <para>A request that names no identity omits the member entirely, so a peer that
///     never sends one serializes exactly as it did before this existed.</para></summary>
public sealed class HostInputIdentityTests
{
  /// <summary>The identity rides the wire and comes back unchanged.</summary>
  [Fact]
  public void TheRequest_CarriesTheIdentity_AndOmitsItWhenUnset()
  {
    RootTurnRequest named = new("turn-1", "hi", []) { UserMessageId = "submit-7" };
    RootTurnRequest anonymous = new("turn-1", "hi", []);

    string namedJson = RootTurnProtocol.SerializeRequest(named);
    string anonymousJson = RootTurnProtocol.SerializeRequest(anonymous);

    Assert.Contains("submit-7", namedJson, StringComparison.Ordinal);
    Assert.DoesNotContain("UserMessageId", anonymousJson, StringComparison.Ordinal);
    Assert.Equal("submit-7", RootTurnProtocol.ParseRequest(namedJson).UserMessageId);
    Assert.Null(RootTurnProtocol.ParseRequest(anonymousJson).UserMessageId);
  }

  /// <summary>Identity is part of structural equality, so a decoded request equals the one
  ///     that was sent and two requests differing only by identity are different turns.</summary>
  [Fact]
  public void TheIdentity_IsPartOfStructuralEquality()
  {
    RootTurnRequest sent = new("turn-1", "hi", []) { UserMessageId = "submit-7" };

    Assert.Equal(sent, RootTurnProtocol.ParseRequest(RootTurnProtocol.SerializeRequest(sent)));
    Assert.NotEqual(sent, sent with { UserMessageId = "submit-8" });
  }
}
