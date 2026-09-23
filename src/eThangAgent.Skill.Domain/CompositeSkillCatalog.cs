using eThangAgent.SharedKernel;

namespace eThangAgent.SkillDomain;

/// <summary>Composite skill catalog: merges built-ins with file skills loaded
/// through <see cref="ISkillDirectorySource" /> from the configured directories.
/// Precedence: built-in, then global-directory skills, then workspace-directory
/// skills; later same-name entries are shadowed (kept for diagnostics, excluded
/// from List and Get). A file skill never shadows a built-in. The load is
/// memoized per instance — the first List/Get/GetDiagnostics call performs one
/// pass over the built-in catalog and every directory; diagnostics are memoized
/// with the load. Source failures degrade to diagnostics, never to a whole-load
/// failure (mirrors SkillListTool's degradation convention).</summary>
public sealed class CompositeSkillCatalog(ISkillCatalog builtIns, ISkillDirectorySource files,
    IReadOnlyList<SkillDirectory> directories) : ISkillCatalog
{
  private const string BuiltInLabel = "built-in";
  private const string GlobalLabel = "global directory";
  private const string WorkspaceLabel = "workspace directory";

  private readonly ISkillCatalog _builtIns = builtIns ?? throw new ArgumentNullException(nameof(builtIns));
  private readonly ISkillDirectorySource _files = files ?? throw new ArgumentNullException(nameof(files));
  private readonly IReadOnlyList<SkillDirectory> _directories =
      directories ?? throw new ArgumentNullException(nameof(directories));
  private readonly Lock _gate = new();
  private CompositeLoad? _load;

  public async Task<Result<IReadOnlyList<SkillDefinition>>> ListAsync(CancellationToken ct = default)
  {
    CompositeLoad load = await EnsureLoadedAsync(ct).ConfigureAwait(false);
    return Result.Success(load.Visible);
  }

  public async Task<Result<SkillDefinition>> GetAsync(string name, CancellationToken ct = default)
  {
    CompositeLoad load = await EnsureLoadedAsync(ct).ConfigureAwait(false);
    return load.Visible.FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.Ordinal))
        is SkillDefinition skill
        ? Result.Success(skill)
        : Result.Failure<SkillDefinition>(new DomainError("SkillNotFound",
            $"No skill named '{name}'. Use skill_list to see available skills."));
  }

  /// <summary>All diagnostics memoized with the load: built-in and per-directory
  /// load failures, per-file diagnostics passed through from each load, and one
  /// collision line per shadowed name —
  /// <c>[collision] &lt;name&gt; (&lt;shadowed label&gt;) shadowed by &lt;winner label&gt;</c>
  /// with labels <c>built-in</c> | <c>global directory</c> | <c>workspace directory</c>.</summary>
  public async Task<Result<IReadOnlyList<string>>> GetDiagnosticsAsync(CancellationToken ct = default)
  {
    CompositeLoad load = await EnsureLoadedAsync(ct).ConfigureAwait(false);
    return Result.Success(load.Diagnostics);
  }

  private async Task<CompositeLoad> EnsureLoadedAsync(CancellationToken ct)
  {
    lock (_gate)
    {
      if (_load is not null)
      {
        return _load;
      }
    }

    CompositeLoad load = await PerformLoadAsync(ct).ConfigureAwait(false);
    lock (_gate)
    {
      _load ??= load;
      return _load;
    }
  }

  private async Task<CompositeLoad> PerformLoadAsync(CancellationToken ct)
  {
    List<string> diagnostics = [];
    List<(int CandidateIndex, string Label, SkillDefinition Skill)> candidates = [];

    Result<IReadOnlyList<SkillDefinition>> builtIn = await _builtIns.ListAsync(ct).ConfigureAwait(false);
    if (builtIn.IsSuccess)
    {
      int index = candidates.Count;
      candidates.AddRange(builtIn.Value.Select(s => (index++, BuiltInLabel, s)));
    }
    else
    {
      diagnostics.Add($"built-in catalog unavailable: {builtIn.Error.Message}");
    }

    foreach (SkillDirectory directory in _directories.Where(d => d.Scope == SkillDirectoryScope.Global)
                 .Concat(_directories.Where(d => d.Scope == SkillDirectoryScope.Workspace)))
    {
      string label = directory.Scope == SkillDirectoryScope.Global ? GlobalLabel : WorkspaceLabel;
      Result<SkillDirectoryLoad> load = await _files.ListAsync(directory.Path, ct).ConfigureAwait(false);
      if (load.IsSuccess)
      {
        diagnostics.AddRange(load.Value.Diagnostics);
        int index = candidates.Count;
        candidates.AddRange(load.Value.Skills.Select(s => (index++, label, s)));
      }
      else
      {
        diagnostics.Add($"directory load failed {directory.Path}: {load.Error.Message}");
      }
    }

    List<(int CandidateIndex, string Line)> collisions = [];
    List<(string Label, SkillDefinition Skill)> winners = [];
    Dictionary<string, int> winnerIndexByName = new(StringComparer.Ordinal);
    HashSet<string> reportedCollisions = new(StringComparer.Ordinal);
    foreach ((int candidateIndex, string label, SkillDefinition skill) in candidates)
    {
      if (winnerIndexByName.TryGetValue(skill.Name, out int winnerIndex))
      {
        if (reportedCollisions.Add(skill.Name))
        {
          collisions.Add((candidateIndex,
              $"[collision] {skill.Name} ({label}) shadowed by {winners[winnerIndex].Label}"));
        }
      }
      else
      {
        winnerIndexByName[skill.Name] = winners.Count;
        winners.Add((label, skill));
      }
    }

    List<SkillDefinition> visible = [.. winners.Where(w => w.Label == BuiltInLabel).Select(w => w.Skill).OrderBy(s => s.Name, StringComparer.Ordinal)
        .Concat(winners.Where(w => w.Label != BuiltInLabel).Select(w => w.Skill).OrderBy(s => s.Name, StringComparer.Ordinal))];
    diagnostics.AddRange(collisions.OrderBy(c => c.CandidateIndex).Select(c => c.Line));
    return new CompositeLoad(visible, [.. diagnostics]);
  }

  private sealed record CompositeLoad(IReadOnlyList<SkillDefinition> Visible, IReadOnlyList<string> Diagnostics);
}
