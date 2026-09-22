using eThangAgent.ToolDomain;

namespace eThangAgent.ComputerUse.ACL.Tests;

/// <summary>Controller-ruled mapping table: every wire code maps to exactly the surface code
///     the tool contract names; unknown codes land on INTERNAL.</summary>
public class BrokerErrorMapperTests
{
  [Fact]
  public void EveryAdvertisedWireCode_MapsToItsSurfaceCode()
  {
    (string Wire, string Surface)[] table =
    [
      ("permission_denied", ComputerErrorCodes.ActionUnavailable),
      ("launch_failed", ComputerErrorCodes.LaunchFailed),
      ("invalid_request", ComputerErrorCodes.InvalidApp),
      ("element_unavailable", ComputerErrorCodes.ElementUnavailable),
      ("not_settable", ComputerErrorCodes.NotSettable),
      ("not_selectable", ComputerErrorCodes.NotSelectable),
      ("action_unavailable", ComputerErrorCodes.ActionUnavailable),
      ("unimplemented", ComputerErrorCodes.ActionUnavailable),
      ("foreground_required", ComputerErrorCodes.ForegroundRequired),
      ("controller_busy", ComputerErrorCodes.ControllerBusy),
      ("internal", ComputerErrorCodes.Internal),
      ("method_not_found", ComputerErrorCodes.Internal),
      ("timeout", ComputerErrorCodes.Timeout),
      ("stale_state", ComputerErrorCodes.StaleState),
      ("version_mismatch", ComputerErrorCodes.VersionMismatch),
    ];
    foreach ((string wire, string surface) in table)
    {
      ComputerOutcome.Failure failure = BrokerErrorMapper.Map(wire, "msg " + wire);
      Assert.Equal(surface, failure.Code);
      Assert.Equal("msg " + wire, failure.Message);
    }
  }

  [Fact]
  public void UnknownWireCode_FailsInternalWithTheVerbatimMessage()
  {
    ComputerOutcome.Failure failure = BrokerErrorMapper.Map("something_novel", "the whole story");
    Assert.Equal(ComputerErrorCodes.Internal, failure.Code);
    Assert.Equal("the whole story", failure.Message);
  }

  [Fact]
  public void NotAuthorized_NeverMappedAsPerCommandOutcome()
  {
    // Handshake-only: the mapper maps it to INTERNAL as a defensive fallback, but the client
    // surfaces it as BrokerAuthException before any command outcome exists. Pinned so the
    // mapping table stays explicit.
    ComputerOutcome.Failure failure = BrokerErrorMapper.Map("not_authorized", "bad token");
    Assert.Equal(ComputerErrorCodes.Internal, failure.Code);
  }

  [Fact]
  public void InputBusy_MapsToTimeoutRetryable_PerLedgeredRuling()
  {
    ComputerOutcome.Failure failure = BrokerErrorMapper.Map("input_busy", "another input operation is in flight.");
    Assert.Equal(ComputerErrorCodes.Timeout, failure.Code);
    Assert.Equal("retry", ComputerErrorCodes.RetryHint(failure.Code));
  }

  [Theory]
  [InlineData("app_not_found", "APP_NOT_FOUND")]
  [InlineData("ambiguous_app", "AMBIGUOUS_APP")]
  public void Map_AppRefFailures_MapToSurfaceCodes(string wireCode, string expectedSurface)
  {
    ComputerOutcome.Failure failure = BrokerErrorMapper.Map(wireCode, "the message");
    Assert.Equal(expectedSurface, failure.Code);
    Assert.Equal("the message", failure.Message);
  }
}
