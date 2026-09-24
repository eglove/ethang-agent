using eThangAgent.SharedKernel;

namespace eThangAgent.SkillDomain;

/// <summary>Diagnostics side-band of the skill catalog: pre-formatted lines
/// describing load degradation and name shadowing. Collision lines already
/// carry the '[collision] ' prefix; every other line is plain diagnostic text
/// the renderer prefixes. Optional capability: catalogs that cannot produce
/// diagnostics simply do not implement it.</summary>
public interface ISkillCatalogDiagnostics
{
  Task<Result<IReadOnlyList<string>>> GetDiagnosticsAsync(CancellationToken ct = default);
}
