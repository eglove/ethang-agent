using eThangAgent.AgentDomain;

namespace eThangAgent.Composition;

/// <summary>Everything a host needs before building the core. ApiKey may be null — each
///     host decides how to present a missing key (Desktop shows a dialog).</summary>
public sealed record AgentSettings(
    string? ApiKey,
    Uri BaseUrl,
    SubAgentOptions SubAgents);
