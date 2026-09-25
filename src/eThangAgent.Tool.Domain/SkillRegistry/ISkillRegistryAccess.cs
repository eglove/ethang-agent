using eThangAgent.SharedKernel;

namespace eThangAgent.ToolDomain;

/// <summary>What to clone and where to stage it (plan #29 task 5). The URL
/// is always HTTPS public; staging NEVER lives inside a configured skill
/// directory — the service owns that invariant.</summary>
public sealed record CloneRequest(Uri CloneUrl, string StagingRoot);

/// <summary>Which staged folder to promote where (plan #29 task 5). Force
/// replaces an existing destination folder.</summary>
public sealed record PromoteRequest(string SourcePath, string TargetDirectory, bool Force);

/// <summary>The filesystem + git seam behind the skill registry (spec #27):
/// shallow-clone into staging, promote a staged skill folder into a target
/// directory, remove one installed skill folder. Implemented in the
/// FileSystem ACL over the native-git adapter pattern; the service in the
/// Tool Domain orchestrates and owns the pipeline semantics.</summary>
public interface ISkillRegistryAccess
{
  /// <summary>Shallow-clones <see cref="CloneRequest.CloneUrl"/> into
  /// <see cref="CloneRequest.StagingRoot"/>; returns the staged root.</summary>
  Task<Result<string>> CloneAsync(CloneRequest request, CancellationToken ct = default);

  /// <summary>Copies the staged folder into
  /// the target directory joined with the staged folder name; returns the destination path. An existing
  /// destination fails DestinationExists unless Force.</summary>
  Task<Result<string>> PromoteAsync(PromoteRequest request, CancellationToken ct = default);

  /// <summary>Deletes one installed skill folder; returns the removed path.
  /// Refuses to delete a configured directory root itself.</summary>
  Task<Result<string>> RemoveAsync(string skillDirectory, CancellationToken ct = default);
}
