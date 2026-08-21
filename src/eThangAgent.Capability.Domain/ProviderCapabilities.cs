namespace eThangAgent.CapabilityDomain;

public sealed record ProviderCapabilities(string Id, IReadOnlyList<ActionDescriptor> Actions);
