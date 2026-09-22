namespace eThangAgent.ToolDomain;

/// <summary>Computer-use host seam: net10.0-windows hosts register a process-wide implementation
///     (the BrokerRegistry-backed singleton) at composition; composition gates the 'computer' tool
///     on settings.ComputerUse and resolves this lazily so every session shares one broker per
///     workspace. Null return means the host cannot provide computer use on this platform.</summary>
public interface IComputerAccessProvider
{
  /// <summary>The process-wide access for the given workspace root; null when unavailable.</summary>
  IComputerAccess? ForWorkspace(string workspaceRoot);
}
