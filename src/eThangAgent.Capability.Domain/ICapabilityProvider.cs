namespace eThangAgent.CapabilityDomain;

public interface ICapabilityProvider
{
    string Id { get; }
    IReadOnlyList<ActionDescriptor> Actions { get; }

    Task<CapabilityInvocationResult> InvokeAsync(
        string actionName, string jsonArguments, CancellationToken ct = default);
}
