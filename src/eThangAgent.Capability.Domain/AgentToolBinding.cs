using eThangAgent.ToolDomain;

namespace eThangAgent.CapabilityDomain;

public sealed record AgentToolBinding(ITool Tool, string Summary);
