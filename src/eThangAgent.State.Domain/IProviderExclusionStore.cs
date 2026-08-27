namespace eThangAgent.StateDomain;

/// <summary>Persistence seam for provider exclusions: model+provider pairs that failed
/// and should be excluded from re-selection until their TTL expires. Owned by the State
/// Domain; implemented by storage ACLs.</summary>
public interface IProviderExclusionStore
{
  Task<IReadOnlySet<string>> GetActiveExclusionsAsync(CancellationToken ct = default);
  Task<bool> AddExclusionAsync(string modelProviderKey, TimeSpan ttl, CancellationToken ct = default);
  Task<bool> RemoveExclusionAsync(string modelProviderKey, CancellationToken ct = default);
}