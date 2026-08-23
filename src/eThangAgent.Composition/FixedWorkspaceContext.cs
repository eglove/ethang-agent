using eThangAgent.StateDomain;

namespace eThangAgent.Composition;

/// <summary>Constant workspace identity for frontends without a workspace concept.
///     Scopes curated-memory writes only; replaced by the future multi-workspace design.</summary>
public sealed class FixedWorkspaceContext(string id) : IWorkspaceContext
{
    public string WorkspaceId { get; } = id;
}
