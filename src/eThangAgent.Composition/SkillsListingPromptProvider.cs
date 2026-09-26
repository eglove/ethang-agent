using eThangAgent.ModelDomain;
using eThangAgent.SharedKernel;
using eThangAgent.SkillDomain;

namespace eThangAgent.Composition;

/// <summary>Budget constants for the always-on skills listing.
///     <see cref="MaxChars" /> covers the ENTIRE rendered block - headers,
///     entries, collision lines, warning lines, and the truncation marker
///     (when entry drops are needed, the marker's current length is reserved
///     before entries are fitted and re-measured each iteration).</summary>
public static class SkillListingBudget
{
  public const int MaxChars = 8000;

  // Keep in sync: format parity with SkillListTool truncation (its private
  // DescriptionLimit in eThangAgent.Tool.Domain) - Tool.Domain cannot reference
  // Composition, so the constant is duplicated deliberately.
  public const int DescriptionLimit = 60;
}

/// <summary>The always-on budgeted skills listing, rendered into every session's
///     system prompt so the model can prefer a matching skill over improvising
///     and load bodies with skill_view (progressive disclosure: names and
///     descriptions only, never bodies). Render contract - implement verbatim:
///
///     [skills listing — prefer a matching skill over improvising; load bodies with skill_view (an exec-bridge call: Tools.Invoke("skill_view", new { name = "&lt;name&gt;" }) inside exec)]
///     ## Built-in
///     - &lt;name&gt;: &lt;description&gt;
///     ## Global directory skills
///     ...
///     ## Workspace directory skills
///     ...
///     ## Learned
///     ...
///     [collision] ... lines (verbatim from GetDiagnosticsAsync)
///     [skills listing truncated: showed &lt;N&gt; of &lt;M&gt; skills; dropped &lt;d&gt; descriptions and &lt;e&gt; entries — call skill_list for the full catalog]
///
///     Rules: MANUAL skills never appear anywhere in the block. Sources map:
///     BuiltIn to the Built-in header; file skills split into the Global/Workspace
///     directory groups by matching SkillDefinition.Origin under a configured
///     directory path (case-insensitive, full-path normalized); an Origin matching
///     no configured directory renders under Global directory skills (defensive
///     default). Descriptions truncate at 60 characters with an appended ellipsis
///     (same as SkillListTool). A group with zero entries renders no header. The
///     budget is SkillListingBudget.MaxChars for the whole block; the overflow
///     algorithm is deterministic - render fully, then while over budget strip
///     descriptions to bare '- &lt;name&gt;' lines lowest-precedence group first
///     (Learned, then Workspace, then Global; Built-in descriptions drop last),
///     within a group from the END of the list; while still over budget drop whole
///     entry lines in the same order; the truncation marker renders whenever ANY
///     drop happened - droppedDescriptions &gt; 0 OR droppedEntries &gt; 0, the
///     never-silent rule (a strips-only overflow announces 'dropped d descriptions
///     and 0 entries'). N/M/d/e count what was shown of the total non-manual skills
///     and how many descriptions/entries were dropped. A
///     catalog or learned load failure renders '[warning] &lt;source&gt; skills
///     unavailable: &lt;message&gt;' and the block still succeeds. A learned skill
///     whose name already exists in the catalog is SKIPPED (presentation-layer
///     merge, spec #19 decision 2) and announced with a
///     '[collision] &lt;name&gt; (learned) shadowed by &lt;built-in|global directory|workspace directory&gt;'
///     line rendered among the diagnostics - the winner label follows the same
///     Origin classification as the groups; when the catalog load fails, dedup
///     is skipped and the Learned group renders whole. A wholly empty
///     result (no skills, no warnings, no collisions) renders the empty string.
///     The built text is memoized per provider instance: the first Build call
///     constructs it, subsequent calls return the cached string.</summary>
public sealed class SkillsListingPromptProvider(ISkillCatalog catalog, ILearnedSkillStore learned,
    IReadOnlyList<SkillDirectory> directories) : ISystemPromptProvider
{
  private const string Header = "[skills listing — prefer a matching skill over improvising; load bodies with skill_view (an exec-bridge call: Tools.Invoke(\"skill_view\", new { name = \"<name>\" }) inside exec)]";
  private const string CollisionPrefix = "[collision] ";

  private readonly ISkillCatalog _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));

  private readonly ILearnedSkillStore _learned = learned ?? throw new ArgumentNullException(nameof(learned));

  private readonly IReadOnlyList<SkillDirectory> _directories = directories ?? throw new ArgumentNullException(nameof(directories));
  private readonly Lock _gate = new();
  private string? _built;

  /// <summary>Builds the listing (memoized per instance; the first call constructs,
  ///     later calls return the cached text). The ISystemPromptProvider seam stays
  ///     synchronous, so hosts that build prompts synchronously block on this call.
  ///     Built-in catalogs resolve synchronously; directory loads are memoized by
  ///     the composite catalog after its first pass.</summary>
  public string Build() => BuildAsync().GetAwaiter().GetResult();

  /// <summary>Hot reload (spec #26): clears the memoized render. The next Build
  ///     re-reads the catalog and re-memoizes; until then, Builds keep serving the
  ///     cached text. Byte-identical behavior when never called.</summary>
  public void Invalidate()
  {
    lock (_gate)
    {
      _built = null;
    }
  }


  public async Task<string> BuildAsync()
  {
    lock (_gate)
    {
      if (_built is not null)
      {
        return _built;
      }
    }

    string built = await BuildCoreAsync(CancellationToken.None).ConfigureAwait(false);
    lock (_gate)
    {
      _built ??= built;
      return _built;
    }
  }

  private async Task<string> BuildCoreAsync(CancellationToken ct)
  {
    List<string> warnings = [];
    List<SkillDefinition> catalogSkills = [];

    Result<IReadOnlyList<SkillDefinition>> fromCatalog = await _catalog.ListAsync(ct).ConfigureAwait(false);
    if (fromCatalog.IsSuccess)
    {
      catalogSkills.AddRange(fromCatalog.Value);
    }
    else
    {
      warnings.Add($"[warning] built-in skills unavailable: {fromCatalog.Error.Message}");
    }

    List<SkillDefinition> learnedSkills = [];
    Result<IReadOnlyList<SkillDefinition>> fromLearned = await _learned.ListAsync(ct).ConfigureAwait(false);
    if (fromLearned.IsSuccess)
    {
      learnedSkills.AddRange(fromLearned.Value);
    }
    else
    {
      warnings.Add($"[warning] learned skills unavailable: {fromLearned.Error.Message}");
    }

    // MANUAL skills never appear anywhere in the block.
    catalogSkills = [.. catalogSkills.Where(s => !s.Manual)];
    learnedSkills = [.. learnedSkills.Where(s => !s.Manual)];

    // Presentation-layer dedup (spec #19 decision 2): a learned skill whose
    // name already exists in the catalog is skipped here and announced - the
    // composite catalog never sees learned rows, so this presentation merge is
    // the only site where the two sources can collide. When the catalog load
    // failed there is nothing to dedup against: the learned group renders
    // whole (the degradation contract).
    List<string> learnedDedupCollisions = [];
    if (fromCatalog.IsSuccess)
    {
      Dictionary<string, string> winnerLabelByName = catalogSkills
          .GroupBy(s => s.Name, StringComparer.Ordinal)
          .ToDictionary(g => g.Key, g => DescribeWinner(g.First()), StringComparer.Ordinal);
      HashSet<string> shadowed = [.. winnerLabelByName.Keys];
      learnedDedupCollisions.AddRange(learnedSkills
          .Where(s => shadowed.Contains(s.Name))
          .Select(s => $"[collision] {s.Name} (learned) shadowed by {winnerLabelByName[s.Name]}"));
      learnedSkills = [.. learnedSkills.Where(s => !shadowed.Contains(s.Name))];
    }

    List<SkillDefinition> builtInGroup = [];
    List<SkillDefinition> globalGroup = [];
    List<SkillDefinition> workspaceGroup = [];
    foreach (SkillDefinition skill in catalogSkills)
    {
      if (skill.Source == SkillSource.BuiltIn)
      {
        builtInGroup.Add(skill);
      }
      else if (Classify(skill.Origin) == SkillDirectoryScope.Global)
      {
        globalGroup.Add(skill);
      }
      else
      {
        workspaceGroup.Add(skill);
      }
    }

    // Render order per the contract: Built-in, Global, Workspace, Learned. The
    // overflow passes iterate the array in REVERSE - lowest precedence first
    // (Learned, then Workspace, then Global; Built-in descriptions drop last) -
    // within a group from the END of the list.
    Group[] groups =
    [
        new Group("Built-in", builtInGroup),
        new Group("Global directory skills", globalGroup),
        new Group("Workspace directory skills", workspaceGroup),
        new Group("Learned", learnedSkills),
    ];

    List<string> diagnosticLines = [];
    if (_catalog is ISkillCatalogDiagnostics withDiagnostics)
    {
      Result<IReadOnlyList<string>> diagnostics = await withDiagnostics.GetDiagnosticsAsync(ct).ConfigureAwait(false);
      if (diagnostics.IsSuccess)
      {
        // Collision lines already carry their '[collision] ' prefix and render
        // verbatim; every OTHER diagnostic (directory load failures, built-in
        // catalog unavailability) becomes '[warning] <text>' - the composite
        // catalog never fails ListAsync, so this side-band is the ONLY signal
        // that a configured directory failed to load.
        diagnosticLines = [.. diagnostics.Value.Select(RenderDiagnostic)];
      }
      else
      {
        warnings.Add($"[warning] catalog diagnostics unavailable: {diagnostics.Error.Message}");
      }
    }

    int total = groups.Sum(g => g.Skills.Count);

    string Render()
    {
      List<string> lines = [Header];
      foreach (Group group in groups)
      {
        bool hasEntries = false;
        for (int i = 0; group.Skills.Count > i; i++)
        {
          if (!group.Present[i])
          {
            continue;
          }

          if (!hasEntries)
          {
            lines.Add("## " + group.Title);
            hasEntries = true;
          }

          lines.Add(group.ShowDescription[i]
              ? $"- {group.Skills[i].Name}: {Truncate(group.Skills[i].Description)}"
              : $"- {group.Skills[i].Name}");
        }
      }

      lines.AddRange(diagnosticLines);
      lines.AddRange(learnedDedupCollisions);
      lines.AddRange(warnings);

      int shown = groups.Sum(g => g.Present.Count(p => p));
      int droppedDescriptions = groups.Sum(g => g.Present.Zip(g.ShowDescription, (present, show) => present && !show ? 1 : 0).Sum());
      int droppedEntries = groups.Sum(g => g.Present.Count(p => !p));
      if (droppedDescriptions > 0 || droppedEntries > 0)
      {
        lines.Add(Marker(shown, total, droppedDescriptions, droppedEntries));
      }

      return string.Join("\n", lines);
    }

    string text = Render();
    if (text.Length <= SkillListingBudget.MaxChars)
    {
      return text.Length == Header.Length ? string.Empty : text;
    }

    // Overflow: the render that just exceeded the budget is not the finish line -
    // each pass re-renders and re-measures, and the loop exits on the first render
    // that fits, so the RETURNED block never exceeds the budget.
    while (true)
    {
      // Pass 1 - strip descriptions, lowest-precedence group first, from the END
      // of each group. Entry lines drop ONLY after every description is gone.
      text = Render();
      if (text.Length <= SkillListingBudget.MaxChars)
      {
        break;
      }

      if (StripDescription(groups))
      {
        continue;
      }

      // Pass 2 - drop whole entry lines in the same order until the block fits;
      // the marker's CURRENT length is reserved while entries drop (re-measured
      // every iteration), so the finished block stays inside the budget.
      if (!DropEntry(groups))
      {
        break; // pathological collision flood: nothing left to drop
      }
    }

    return Render();
  }

  /// <summary>Full-path normalization for Origin-to-directory matching: canonical
  ///     case-insensitive form, trailing separators trimmed; an unresolvable path
  ///     simply stays itself (uppercased, trimmed). Deliberately NOT the session
  ///     factory's normalization (that one resolves relative paths against the
  ///     workspace root); production data is absolute on both sides - Origins are
  ///     skill-folder full paths and configured directories are resolved absolute -
  ///     so the two normalizations agree on every real input.</summary>
  private static string Normalize(string path)
  {
    try
    {
      return Path.GetFullPath(path)
          .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
          .ToUpperInvariant();
    }
    catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException or System.Security.SecurityException)
    {
      return path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).ToUpperInvariant();
    }
  }

  /// <summary>Classifies a file skill to its directory group by matching its
  ///     Origin under a configured directory path (case-insensitive, full-path
  ///     normalized). An Origin matching no configured directory defaults to the
  ///     Global group (defensive default per the controller ruling). Origin is the
  ///     skill folder path, so the directory is either a prefix of the Origin or
  ///     the Origin itself.</summary>
  private SkillDirectoryScope Classify(string? origin)
  {
    if (!string.IsNullOrWhiteSpace(origin))
    {
      string normalizedOrigin = Normalize(origin);
      foreach (SkillDirectory directory in _directories)
      {
        string normalizedDirectory = Normalize(directory.Path);
        if (normalizedOrigin.StartsWith(normalizedDirectory, StringComparison.Ordinal)
            && (normalizedOrigin.Length == normalizedDirectory.Length
                || normalizedOrigin[normalizedDirectory.Length] == Path.DirectorySeparatorChar
                || normalizedOrigin[normalizedDirectory.Length] == Path.AltDirectorySeparatorChar))
        {
          return directory.Scope;
        }
      }
    }

    return SkillDirectoryScope.Global;
  }

  /// <summary>The label for the dedup collision line: the source whose row wins
  ///     over the skipped learned row - built-in, or the directory scope class.</summary>
  private string DescribeWinner(SkillDefinition skill)
  {
    if (skill.Source == SkillSource.BuiltIn)
    {
      return "built-in";
    }

    bool global = Classify(skill.Origin) == SkillDirectoryScope.Global;
    return global ? "global directory" : "workspace directory";
  }

  /// <summary>Collision lines already carry their '[collision] ' prefix and render
  ///     verbatim; every other diagnostic is plain text that gains the '[warning] '
  ///     prefix here (the SkillListTool convention).</summary>
  private static string RenderDiagnostic(string line) =>
      line.StartsWith(CollisionPrefix, StringComparison.Ordinal) ? line : "[warning] " + line;

  private static string Truncate(string description) =>
      description.Length <= SkillListingBudget.DescriptionLimit
          ? description
          : description[..SkillListingBudget.DescriptionLimit] + '…';

  private static string Marker(int shown, int total, int droppedDescriptions, int droppedEntries) =>
      $"[skills listing truncated: showed {shown} of {total} skills; dropped {droppedDescriptions} descriptions and {droppedEntries} entries — call skill_list for the full catalog]";

  /// <summary>One description-stripping step: the next bare '- name' conversion,
  ///     lowest-precedence group first, within a group from the END of the list.
  ///     Returns false when no shown description remains.</summary>
  private static bool StripDescription(Group[] groups)
  {
    for (int gi = groups.Length - 1; 0 <= gi; gi--)
    {
      Group group = groups[gi];
      for (int i = group.Skills.Count - 1; 0 <= i; i--)
      {
        if (group.Present[i] && group.ShowDescription[i])
        {
          group.ShowDescription[i] = false;
          return true;
        }
      }
    }

    return false;
  }

  /// <summary>One entry-dropping step: removes one whole entry line - only after
  ///     every description is stripped - in the same group order, from the END of
  ///     the list. Returns false when no entry remains.</summary>
  private static bool DropEntry(Group[] groups)
  {
    for (int gi = groups.Length - 1; 0 <= gi; gi--)
    {
      Group group = groups[gi];
      for (int i = group.Skills.Count - 1; 0 <= i; i--)
      {
        if (group.Present[i])
        {
          group.Present[i] = false;
          return true;
        }
      }
    }

    return false;
  }

  private sealed class Group(string header, List<SkillDefinition> skills)
  {
    public string Title { get; } = header;

    public List<SkillDefinition> Skills { get; } = skills;

    public List<bool> Present { get; } = [.. skills.Select(_ => true)];

    public List<bool> ShowDescription { get; } = [.. skills.Select(_ => true)];
  }
}
