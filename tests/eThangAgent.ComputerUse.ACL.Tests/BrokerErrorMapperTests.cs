using eThangAgent.ToolDomain;

namespace eThangAgent.ComputerUse.ACL.Tests;

/// <summary>Error mapping table (spec 7): every wire code maps to exactly the surface code
///     the tool contract names; unknown codes land on INTERNAL.</summary>
public class BrokerErrorMapperTests
{
  [Fact]
  public void EveryAdvertisedWireCode_MapsToItsSurfaceCode()
  {
    (string Wire, string Surface)[] table =
    [
      ("permission_denied", ComputerErrorCodes.AppNotFound),
      ("launch_failed", ComputerErrorCodes.LaunchFailed),
      ("invalid_request", ComputerErrorCodes.InvalidApp),
      ("element_unavailable", ComputerErrorCodes.ElementUnavailable),
      ("not_settable", ComputerErrorCodes.NotSettable),
      ("not_selectable", ComputerErrorCodes.NotSelectable),
      ("action_unavailable", ComputerErrorCodes.ActionUnavailable),
      ("foreground_required", ComputerErrorCodes.ForegroundRequired),
      ("controller_busy", ComputerErrorCodes.ControllerBusy),
      ("internal", ComputerErrorCodes.Internal),
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
}
