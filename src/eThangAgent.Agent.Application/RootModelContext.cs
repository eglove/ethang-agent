using eThangAgent.AgentDomain;
using eThangAgent.ModelDomain;

namespace eThangAgent.Agent.Application;

/// <summary>Session-level collaborators and budgets shared by the root model resolvers:
///     transcript persistence, the session's root identity, the fallback model that
///     serves every unresolved turn, the serving budgets, and the context-window
///     source. One value per session; keeps the resolvers' constructors parameter-
///     light (S107) without loosening any null-guard — each member is validated
///     where it is bound to a field. <paramref name="Catalog"/> is optional: when
///     wired, resolvers stamp the model's vision capability onto every resolved
///     config; when null, the capability defaults false (byte-identical legacy
///     behavior for tests and legacy wiring).</summary>
public sealed record RootModelContext(
    IAgentStore? Store,
    RootSessionIdentity? Identity,
    string FallbackModelId,
    int MaxTokens,
    float Temperature,
    IContextWindowSource? WindowSource,
    IModelCatalog? Catalog = null);
