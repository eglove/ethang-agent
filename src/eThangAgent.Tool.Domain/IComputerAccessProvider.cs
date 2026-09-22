namespace eThangAgent.ToolDomain;

/// <summary>Computer-use host seam: net10.0-windows hosts register the BrokerRegistry-backed
///     provider (one broker per workspace root, shared by every session in it) at composition;
///     composition gates the 'computer' tool on settings.ComputerUse. A null return makes the
///     composition fall back to NullComputerAccess (typed HELPER_UNAVAILABLE) - it is legal only
///     for hosts that never enable the tool.</summary>
public interface IComputerAccessProvider
{
  /// <summary>The process-wide access for the given workspace root; null when unavailable.</summary>
  IComputerAccess? ForWorkspace(string workspaceRoot);
}
