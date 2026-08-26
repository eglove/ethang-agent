using eThangAgent.ModelDomain;
using eThangAgent.StateDomain;
using eThangAgent.ToolDomain;

namespace eThangAgent.Composition;

/// <summary>The presentation-scoped decisions a frontend supplies to the shared core.
///     Everything else about hosting is identical across frontends. <see cref="ExtraPromptProviders"/>
///     lets a frontend append its own system-prompt sections (e.g. workspace instructions,
///     built once at startup); they render after the core providers in the composite prompt.</summary>
public sealed class AgentHostOptions(
    IClarifyChannel clarifyChannel,
    IWorkspaceContext workspaceContext,
    IPathResolver pathResolver,
    IReadOnlyList<ISystemPromptProvider>? extraPromptProviders = null)
{
  public IClarifyChannel ClarifyChannel { get; } = clarifyChannel ?? throw new ArgumentNullException(nameof(clarifyChannel));

  public IWorkspaceContext WorkspaceContext { get; } = workspaceContext ?? throw new ArgumentNullException(nameof(workspaceContext));

  public IPathResolver PathResolver { get; } = pathResolver ?? throw new ArgumentNullException(nameof(pathResolver));

  /// <summary>Frontend-appended system prompt providers; never null, possibly empty.</summary>
  public IReadOnlyList<ISystemPromptProvider> ExtraPromptProviders { get; } = extraPromptProviders ?? [];
}
