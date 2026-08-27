namespace eThangAgent.AgentDomain;

public interface IProviderExclusionStore
{
  Task<IReadOnlySet<string>> GetActiveExclusionsAsync(CancellationToken ct = default);
  Task<bool> AddExclusionAsync(string modelProviderKey, TimeSpan ttl, CancellationToken ct = default);
  Task<bool> RemoveExclusionAsync(string modelProviderKey, CancellationToken ct = default);
}