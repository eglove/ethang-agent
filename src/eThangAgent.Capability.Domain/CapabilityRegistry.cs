using eThangAgent.SharedKernel;

namespace eThangAgent.CapabilityDomain;

public sealed class CapabilityRegistry : ICapabilityRegistry
{
    private readonly Dictionary<string, ICapabilityProvider> _providersById;
    private readonly Dictionary<string, (ICapabilityProvider Provider, ActionDescriptor Action)> _byName;
    private readonly IReadOnlyList<ProviderCapabilities> _providers;

    private CapabilityRegistry(IReadOnlyList<ICapabilityProvider> providers)
    {
        _providersById = providers.ToDictionary(p => p.Id, StringComparer.Ordinal);
        _providers = providers.Select(p => new ProviderCapabilities(p.Id, p.Actions)).ToList();
        _byName = providers
            .SelectMany(p => p.Actions.Select(a => (Provider: p, Action: a)))
            .ToDictionary(x => x.Action.Name, x => (x.Provider, x.Action), StringComparer.Ordinal);
    }

    public static CapabilityRegistry Create(IEnumerable<ICapabilityProvider> providers)
    {
        var list = providers.ToList();

        foreach (var provider in list)
        {
            if (string.IsNullOrWhiteSpace(provider.Id))
                throw new InvalidOperationException(
                    $"Capability provider id must be non-empty ({provider.GetType().Name}).");
            if (list.Count(p => p.Id == provider.Id) > 1)
                throw new InvalidOperationException($"Duplicate capability provider id '{provider.Id}'.");
            if (provider.Actions.Count == 0)
                throw new InvalidOperationException($"Capability provider '{provider.Id}' exposes no actions.");
            foreach (var action in provider.Actions)
            {
                if (!CapabilityNameRules.IsValidActionName(action.Name))
                    throw new InvalidOperationException(
                        $"Action name '{action.Name}' in provider '{provider.Id}' is invalid; " +
                        "use [A-Za-z0-9_] only.");
            }
        }

        var duplicate = list.SelectMany(p => p.Actions.Select(a => a.Name))
            .GroupBy(n => n, StringComparer.Ordinal)
            .FirstOrDefault(g => g.Count() > 1);
        if (duplicate is not null)
            throw new InvalidOperationException(
                $"Duplicate action name '{duplicate.Key}' across capability providers.");

        return new CapabilityRegistry(list);
    }

    public IReadOnlyList<ProviderCapabilities> Providers => _providers;

    public Result<ResolvedCapability> Resolve(string nameOrRef)
    {
        if (_byName.TryGetValue(nameOrRef, out var direct))
            return Result<ResolvedCapability>.Success(
                new ResolvedCapability(direct.Provider.Id, direct.Action));

        var dot = nameOrRef.IndexOf('.');
        if (dot > 0 && dot < nameOrRef.Length - 1)
        {
            var providerId = nameOrRef[..dot];
            var actionName = nameOrRef[(dot + 1)..];
            if (_providersById.TryGetValue(providerId, out var provider))
            {
                var action = provider.Actions.FirstOrDefault(a => a.Name == actionName);
                if (action is not null)
                    return Result<ResolvedCapability>.Success(new ResolvedCapability(providerId, action));
            }
        }

        return Result<ResolvedCapability>.Failure(new Error("UnknownAction",
            $"Unknown action '{nameOrRef}'. Available: {string.Join(", ", _byName.Keys.OrderBy(k => k))}."));
    }

    public async Task<CapabilityInvocationResult> InvokeAsync(
        ResolvedCapability capability, string jsonArguments, CancellationToken ct = default)
    {
        var provider = _providersById[capability.ProviderId];
        return await provider.InvokeAsync(capability.Action.Name, jsonArguments, ct);
    }
}
