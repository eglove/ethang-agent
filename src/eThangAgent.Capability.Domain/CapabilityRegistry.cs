using eThangAgent.SharedKernel;

namespace eThangAgent.CapabilityDomain;

public sealed class CapabilityRegistry : ICapabilityRegistry
{
  private readonly Dictionary<string, ICapabilityProvider> _providersById;
  private readonly Dictionary<string, (ICapabilityProvider Provider, ActionDescriptor Action)> _byName;

  private CapabilityRegistry(IReadOnlyList<ICapabilityProvider> providers)
  {
    _providersById = providers.ToDictionary(p => p.Id, StringComparer.Ordinal);
    Providers = [.. providers.Select(p => new ProviderCapabilities(p.Id, p.Actions))];
    _byName = providers
        .SelectMany(p => p.Actions.Select(a => (Provider: p, Action: a)))
        .ToDictionary(x => x.Action.Name, x => (x.Provider, x.Action), StringComparer.Ordinal);
  }

  public static CapabilityRegistry Create(IEnumerable<ICapabilityProvider> providers)
  {
    List<ICapabilityProvider> list = [.. providers];

    foreach (ICapabilityProvider? provider in list)
    {
      if (string.IsNullOrWhiteSpace(provider.Id))
      {
        throw new InvalidOperationException(
            $"Capability provider id must be non-empty ({provider.GetType().Name}).");
      }

      if (list.Count(p => p.Id == provider.Id) > 1)
      {
        throw new InvalidOperationException($"Duplicate capability provider id '{provider.Id}'.");
      }

      if (provider.Actions.Count == 0)
      {
        throw new InvalidOperationException($"Capability provider '{provider.Id}' exposes no actions.");
      }

      foreach (ActionDescriptor action in provider.Actions)
      {
        if (!CapabilityNameRules.IsValidActionName(action.Name))
        {
          throw new InvalidOperationException(
              $"Action name '{action.Name}' in provider '{provider.Id}' is invalid; " +
              "use [A-Za-z0-9_] only.");
        }
      }
    }

    IGrouping<string, string>? duplicate = list.SelectMany(p => p.Actions.Select(a => a.Name))
        .GroupBy(n => n, StringComparer.Ordinal)
        .FirstOrDefault(g => g.Count() > 1);
    return duplicate is not null
      ? throw new InvalidOperationException(
          $"Duplicate action name '{duplicate.Key}' across capability providers.")
      : new CapabilityRegistry(list);
  }

  public IReadOnlyList<ProviderCapabilities> Providers { get; }

  public Result<ResolvedCapability> Resolve(string nameOrRef)
  {
    ArgumentNullException.ThrowIfNull(nameOrRef);
    if (_byName.TryGetValue(nameOrRef, out (ICapabilityProvider Provider, ActionDescriptor Action) direct))
    {
      return Result.Success<ResolvedCapability>(
          new ResolvedCapability(direct.Provider.Id, direct.Action));
    }

    int dot = nameOrRef.IndexOf('.', StringComparison.Ordinal);
    if (dot > 0 && dot < nameOrRef.Length - 1)
    {
      string providerId = nameOrRef[..dot];
      string actionName = nameOrRef[(dot + 1)..];
      if (_providersById.TryGetValue(providerId, out ICapabilityProvider? provider))
      {
        ActionDescriptor? action = provider.Actions.FirstOrDefault(a => a.Name == actionName);
        if (action is not null)
        {
          return Result.Success<ResolvedCapability>(new ResolvedCapability(providerId, action));
        }
      }
    }

    return Result.Failure<ResolvedCapability>(new DomainError("UnknownAction",
        $"Unknown action '{nameOrRef}'. Available: {string.Join(", ", _byName.Keys.OrderBy(k => k))}."));
  }

  public async Task<CapabilityInvocationResult> InvokeAsync(
      ResolvedCapability capability, string jsonArguments, CancellationToken ct = default)
  {
    ArgumentNullException.ThrowIfNull(capability);
    ICapabilityProvider provider = _providersById[capability.ProviderId];
    return await provider.InvokeAsync(capability.Action.Name, jsonArguments, ct).ConfigureAwait(false);
  }
}
