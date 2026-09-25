namespace eThangAgent.SkillDomain;

/// <summary>Hot reload (spec #26): the seam the watcher drives. One pass re-reads
/// every source and diffs the visible sets; the result is the announcement view.</summary>
public interface IReloadableSkillCatalog
{
  Task<SharedKernel.Result<SkillReloadDiff>> ReloadAsync(CancellationToken ct = default);
}
