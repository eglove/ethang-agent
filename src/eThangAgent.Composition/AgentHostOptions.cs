using eThangAgent.ModelDomain;
using eThangAgent.StateDomain;
using eThangAgent.ToolDomain;

namespace eThangAgent.Composition;

/// <summary>The presentation-scoped decisions a frontend supplies to the shared core.
///     Everything else about hosting is identical across frontends. <see cref="ExtraPromptProviders"/>
///     lets a frontend append its own system-prompt sections (e.g. workspace instructions,
///     built once at startup); they render after the core providers in the composite prompt.</summary>
public sealed class AgentHostOptions
{
    public AgentHostOptions(
        IClarifyChannel clarifyChannel,
        IWorkspaceContext workspaceContext,
        IPathResolver pathResolver,
        IReadOnlyList<ISystemPromptProvider>? extraPromptProviders = null)
    {
        ClarifyChannel = clarifyChannel ?? throw new ArgumentNullException(nameof(clarifyChannel));
        WorkspaceContext = workspaceContext ?? throw new ArgumentNullException(nameof(workspaceContext));
        PathResolver = pathResolver ?? throw new ArgumentNullException(nameof(pathResolver));
        ExtraPromptProviders = extraPromptProviders ?? [];
    }

    public IClarifyChannel ClarifyChannel { get; }

    public IWorkspaceContext WorkspaceContext { get; }

    public IPathResolver PathResolver { get; }

    /// <summary>Frontend-appended system prompt providers; never null, possibly empty.</summary>
    public IReadOnlyList<ISystemPromptProvider> ExtraPromptProviders { get; }
}
