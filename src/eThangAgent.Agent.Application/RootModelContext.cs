using eThangAgent.AgentDomain;
using eThangAgent.ModelDomain;

namespace eThangAgent.Agent.Application;

/// <summary>Session-level collaborators and budgets shared by the root model resolvers:
///     transcript persistence, the session's root identity, the fallback model that
///     serves every unresolved turn, the serving budgets, and the context-window
///     source. One value per session; keeps the resolvers' constructors parameter-
///     light (S107) without loosening any null-guard — each member is validated
///     where it is bound to a field.</summary>
public sealed record RootModelContext(
    IAgentStore? Store,
    RootSessionIdentity? Identity,
    string FallbackModelId,
    int MaxTokens,
    float Temperature,
    IContextWindowSource? WindowSource);
