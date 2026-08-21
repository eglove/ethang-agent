namespace eThangAgent.CapabilityDomain;

/// <summary>Aggregates several capability providers under one provider id, merging their action lists and dispatching invocations to the owning source.</summary>
public sealed class MergedCapabilityProvider(string id, IReadOnlyList<ICapabilityProvider> sources) : ICapabilityProvider
{
    private readonly IReadOnlyList<ICapabilityProvider> _sources = sources ?? throw new ArgumentNullException(nameof(sources));

    public string Id => id;

    public IReadOnlyList<ActionDescriptor> Actions =>
        _sources.SelectMany(s => s.Actions).ToArray();

    public async Task<CapabilityInvocationResult> InvokeAsync(
        string actionName, string jsonArguments, CancellationToken ct = default)
    {
        foreach (var source in _sources)
        {
            if (source.Actions.Any(a => a.Name == actionName))
                return await source.InvokeAsync(actionName, jsonArguments, ct);
        }

        return CapabilityInvocationResult.Fail($"Error [UnknownAction]: Unknown action: {actionName}.");
    }
}