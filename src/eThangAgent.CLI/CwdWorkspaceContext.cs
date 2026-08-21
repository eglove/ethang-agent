using eThangAgent.StateDomain;

namespace eThangAgent.CLI;

public sealed class CwdWorkspaceContext : IWorkspaceContext
{
    public string WorkspaceId { get; } = Path.GetFullPath(".");
}
