using System.Text.Json;

namespace eThangAgent.ComputerUse.Host.Tests;

/// <summary>Fix round 5 (F2/F5a): the observation envelope must never fabricate data.
///     F2: a walk that hits its deadline (null walk) answers the typed retryable
///     timeout wire error - not a valid-looking empty full snapshot. F5a: an app-ref
///     resolution failure travels as a PROPER error envelope (app_not_found /
///     ambiguous_app) so the ACL's BrokerErrorMapper translates it - not
///     error-inside-a-200-result. Both are proven against an injectable walk seam,
///     never a live desktop.</summary>
public class ObservationEnvelopeContractTests
{
  [Fact]
  public void CaptureApp_TimedOutWalk_TypedRetryableTimeoutError_NotEmptyFullSnapshot()
  {
    // The injectable walk times out (WalkElements => null); resolution is pinned to
    // succeed so the WALK's deadline behavior is the only variable under test.
    TimedOutObserver observer = new();
    PipeServer server = new(new BrokerConfig("ignored-pipe", "t"), observer);
    _ = server.Dispatch(0, "authenticate", JsonDocument.Parse("{\"token\":\"t\"}").RootElement, connectionId: 1);
    BrokerResponse reply = server.Dispatch(2, "capture_app",
      JsonDocument.Parse("{\"app_ref\":{\"pid\":424242}}").RootElement, connectionId: 1);
    _ = Assert.NotNull(reply.Error);
    Assert.Equal("timeout", reply.Error.Value.Code);
    Assert.Contains("deadline", reply.Error.Value.Message, StringComparison.Ordinal);
  }

  [Fact]
  public void CaptureApp_UnknownPid_ErrorEnvelopeAppNotFound_NotErrorInsideOk()
  {
    RealBrokerObserver observer = new();
    PipeServer server = new(new BrokerConfig("ignored-pipe", "t"), observer);
    _ = server.Dispatch(0, "authenticate", JsonDocument.Parse("{\"token\":\"t\"}").RootElement, connectionId: 1);
    BrokerResponse reply = server.Dispatch(2, "capture_app",
      JsonDocument.Parse("{\"app_ref\":{\"pid\":999999}}").RootElement, connectionId: 1);
    _ = Assert.NotNull(reply.Error);
    Assert.Equal("app_not_found", reply.Error.Value.Code);
  }

  [Fact]
  public void CaptureApp_StopOwner_ReceivesAReceiptNotAnOwnedFlag()
  {
    // F5b: the owner's stop_computer_control reply is a receipt
    // {action_sent:true, dispatch_status:accepted}; the non-owner refusal keeps
    // the round-1 invalid_request shape (pinned in PipeServerDispatchTests).
    PipeServer server = FakeConnectionFactory.Authorized(1);
    _ = server.Dispatch(2, "controller_takeover", null, connectionId: 1);
    BrokerResponse reply = server.Dispatch(3, "stop_computer_control", null, connectionId: 1);
    Assert.Null(reply.Error);
    _ = Assert.NotNull(reply.Result);
    Assert.True(reply.Result.Value.TryGetProperty("action_sent", out JsonElement sent) && sent.ValueKind == JsonValueKind.True);
    Assert.True(reply.Result.Value.TryGetProperty("dispatch_status", out JsonElement status)
      && status.ValueKind == JsonValueKind.String && status.GetString() == "accepted");
    bool hasOwned = reply.Result.Value.TryGetProperty("owned", out _);
    Assert.False(hasOwned);
  }
}

/// <summary>An observer whose walk always times out: resolution is pinned to a fixed
///     fake window and the walk seam answers null (the deadline always wins). Nothing
///     is fabricated - the timeout envelope is the only honest answer.</summary>
internal sealed class TimedOutObserver : RealBrokerObserver
{
  internal override bool TryResolveAppRef(int pid, string? name, string? aumid, int? windowId,
      out AppRefResolution resolution, out AppRefResolutionFailure? failure)
  {
    resolution = new AppRefResolution(0x1234, pid);
    failure = null;
    return true;
  }

  internal override WalkResult? WalkElements(nint window) => null; // the deadline always wins
}
