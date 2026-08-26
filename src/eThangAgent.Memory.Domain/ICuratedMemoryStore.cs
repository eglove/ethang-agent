using eThangAgent.SharedKernel;

namespace eThangAgent.MemoryDomain;

/// <summary>Typed error codes surfaced by curated-memory persistence.</summary>
public static class CuratedMemoryErrors
{
  /// <summary>CAS update failed; the error message names the current stored version.</summary>
  public const string VersionConflict = "VersionConflict";

  /// <summary>No row exists for the requested id.</summary>
  public const string MemoryNotFound = "MemoryNotFound";

  /// <summary>The store could not complete the operation.</summary>
  public const string StorageError = "StorageError";
}

/// <summary>
/// The seam for durable, workspace-scoped curated memories. Implementations own
/// storage mechanics; the domain only knows these operations and their typed
/// errors (<see cref="CuratedMemoryErrors"/>).
/// </summary>
public interface ICuratedMemoryStore
{
  Task<Result<CuratedMemory>> AddAsync(CuratedMemory memory, CancellationToken ct = default);

  /// <summary>The memory with the given id, or null when absent — absence is a query answer, not an error.</summary>
  Task<Result<CuratedMemory?>> GetAsync(Guid id, CancellationToken ct = default);

  /// <summary>Ranked: query matches via FTS when non-empty, else newest-updated first.
  /// Rows visible: scope Global always; scope Workspace only when workspaceId matches.</summary>
  Task<Result<IReadOnlyList<CuratedMemory>>> SearchAsync(
      string? workspaceId, string? query, MemoryCategory? category,
      IReadOnlyList<string>? tags, int limit, CancellationToken ct = default);

  /// <summary>CAS: fails VersionConflict unless memory.Version equals stored version + 1
  /// (the proposed next version); the error names the current stored version.</summary>
  Task<Result<CuratedMemory>> UpdateAsync(CuratedMemory updated, CancellationToken ct = default);

  Task<Result<bool>> DeleteAsync(Guid id, CancellationToken ct = default);
}
