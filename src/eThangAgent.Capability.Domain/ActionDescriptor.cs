namespace eThangAgent.CapabilityDomain;

public sealed record ActionDescriptor(
    string Name,
    string Summary,
    string Description,
    IReadOnlyList<ActionParameter> Parameters,
    IReadOnlyList<string>? RequiredParameters = null);
