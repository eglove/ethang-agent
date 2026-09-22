namespace eThangAgent.ToolDomain;

/// <summary>Computer-use host seam: net10.0-windows hosts register the BrokerRegistry-backed
///     provider (one broker per workspace root, shared by every session in it) at composition;
///     composition gates the 'computer' tool on settings.ComputerUse. There is NO null fallback
///     in production: the composition's ComputerToolBindings dereferences the provider's return
///     whenever the tool is enabled, so a null is legal ONLY for hosts that never enable the
///     tool (they never construct a provider whose ForWorkspace would return null).</summary>
public interface IComputerAccessProvider
{
  /// <summary>The process-wide access for the given workspace root; null when unavailable.</summary>
  IComputerAccess? ForWorkspace(string workspaceRoot);
}
