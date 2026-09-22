using eThangAgent.ToolDomain;

namespace eThangAgent.ComputerUse.ACL;

/// <summary>Maps broker wire codes (spec 7) onto the Tool Domain surface codes the
///     computer tool renders. Unknown codes land on INTERNAL - the conservative,
///     optimistic-retry default of the surface's hint table. not_authorized is NOT mapped
///     here: it is a handshake failure, surfaced as VERSION-style fatal connection errors
///     by the client, never as a per-command outcome. Transport loss (pipe broke, broker
///     gone) is the client's HELPER_UNAVAILABLE, not a mapper case.</summary>
public static class BrokerErrorMapper
{
  /// <summary>Maps one wire code + message to the surface failure. The message travels
  ///     verbatim - the broker wrote it for the model.</summary>
  public static ComputerOutcome.Failure Map(string wireCode, string message) => new(Surface(wireCode), message);

  private static string Surface(string wireCode) => wireCode switch
  {
    "permission_denied" => ComputerErrorCodes.ActionUnavailable,
    "launch_failed" => ComputerErrorCodes.LaunchFailed,
    "invalid_request" => ComputerErrorCodes.InvalidApp,
    "element_unavailable" => ComputerErrorCodes.ElementUnavailable,
    "not_settable" => ComputerErrorCodes.NotSettable,
    "not_selectable" => ComputerErrorCodes.NotSelectable,
    "action_unavailable" => ComputerErrorCodes.ActionUnavailable,
    "foreground_required" => ComputerErrorCodes.ForegroundRequired,
    "input_busy" => ComputerErrorCodes.Timeout, // ledgered ruling: retryable, action_sent=false
    "controller_busy" => ComputerErrorCodes.ControllerBusy,
    "internal" => ComputerErrorCodes.Internal,
    "method_not_found" => ComputerErrorCodes.Internal,
    "unimplemented" => ComputerErrorCodes.ActionUnavailable,
    "timeout" => ComputerErrorCodes.Timeout,
    "stale_state" => ComputerErrorCodes.StaleState,
    "version_mismatch" => ComputerErrorCodes.VersionMismatch,
    _ => ComputerErrorCodes.Internal,
  };
}
