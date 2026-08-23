using eThangAgent.StateDomain;
using eThangAgent.ToolDomain;

namespace eThangAgent.Composition;

/// <summary>The three presentation-scoped decisions a frontend supplies to the shared core.
///     Everything else about hosting is identical across frontends.</summary>
public sealed record AgentHostOptions(
    IClarifyChannel ClarifyChannel,
    IWorkspaceContext WorkspaceContext,
    IPathResolver PathResolver);
