using eThangAgent.AgentDomain;
using eThangAgent.ModelDomain;

namespace eThangAgent.Agent.Application;

/// <summary>Session-level collaborators and budgets shared by the root model resolvers:
///     transcript persistence, the session's root identity, the fallback model, the serving
///     budgets, the context-window source, and the vendor-keyed catalog resolution.
///     One value per session; keeps the resolvers' constructors parameter-light (S107)
///     without loosening any null-guard - each member is validated where it is bound to a
///     field.
///     <para>THE CATALOG IS RESOLVED PER VENDOR, NOT HELD AS ONE. A session's ordered model
///     list names the vendor that serves each entry, and a session is not tied to a provider:
///     a single catalog here would answer capability questions for whichever vendor the
///     session happened to be opened under, and report every other vendor's models as unknown.
///     </para>
///     <para><paramref name="Resolver"/> is optional: when wired, resolvers stamp the model's
///     vision capability onto every resolved config; when null, the capability defaults false
///     (byte-identical legacy behavior for tests and legacy wiring).</para></summary>
public sealed record RootModelContext(
    IAgentStore? Store,
    RootSessionIdentity? Identity,
    string FallbackModelId,
    int MaxTokens,
    float Temperature,
    IContextWindowSource? WindowSource,
    IModelProviderResolver? Resolver = null);
