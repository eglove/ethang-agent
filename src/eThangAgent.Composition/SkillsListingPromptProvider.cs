using eThangAgent.ModelDomain;
using eThangAgent.SharedKernel;
using eThangAgent.SkillDomain;

namespace eThangAgent.Composition;

/// <summary>Budget constants for the always-on skills listing.
///     <see cref="MaxChars" /> covers the ENTIRE rendered block - headers,
///     entries, collision lines, warning lines, and the truncation marker
///     (the marker's length is reserved before entries are fitted).</summary>
public static class SkillListingBudget
{
  public const int MaxChars = 8000;
  public const int DescriptionLimit = 60;
}

/// <summary>The always-on budgeted skills listing, rendered into every session's
///     system prompt so the model can prefer a matching skill over improvising
///     and load bodies with skill_view (progressive disclosure: names and
///     descriptions only, never bodies). Render contract - implement verbatim:
///
///     [skills listing — prefer a matching skill over improvising; load bodies with skill_view]
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
///     entry lines in the same order; the truncation marker ALWAYS renders (its
///     length is reserved inside the budget). N/M/d/e count what was shown of the
///     total non-manual skills and how many descriptions/entries were dropped. A
///     catalog or learned load failure renders '[warning] &lt;source&gt; skills
///     unavailable: &lt;message&gt;' and the block still succeeds; a wholly empty
///     result (no skills, no warnings, no collisions) renders the empty string.
///     The built text is memoized per provider instance: the first Build call
///     constructs it, subsequent calls return the cached string.</summary>
public sealed class SkillsListingPromptProvider(ISkillCatalog catalog, ILearnedSkillStore learned,
    IReadOnlyList<SkillDirectory> directories) : ISystemPromptProvider
{
  private const string Header = "[skills listing — prefer a matching skill over improvising; load bodies with skill_view]";
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

    List<string> collisionLines = [];
    if (_catalog is ISkillCatalogDiagnostics withDiagnostics)
    {
      Result<IReadOnlyList<string>> diagnostics = await withDiagnostics.GetDiagnosticsAsync(ct).ConfigureAwait(false);
      collisionLines = diagnostics.IsSuccess
          ? [.. diagnostics.Value.Where(l => l.StartsWith(CollisionPrefix, StringComparison.Ordinal))]
          : [];
      if (!diagnostics.IsSuccess)
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

      lines.AddRange(collisionLines);
      lines.AddRange(warnings);

      int shown = groups.Sum(g => g.Present.Count(p => p));
      int droppedDescriptions = groups.Sum(g => g.Present.Zip(g.ShowDescription, (present, show) => present && !show ? 1 : 0).Sum());
      int droppedEntries = groups.Sum(g => g.Present.Count(p => !p));
      if (shown < total)
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

    // Overflow: reserve the marker's WORST-CASE length (nothing yet dropped -
    // the digit counts only shrink as passes proceed) plus its newline, so the
    // finished block never exceeds the budget.
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

      // Pass 2 - drop whole entry lines in the same order until the block fits.
      if (!DropEntry(groups))
      {
        break; // pathological collision flood: nothing left to drop
      }
    }

    return Render();
  }

  /// <summary>Full-path normalization for Origin-to-directory matching (the same
  ///     normalization the session factory applies to the configured paths):
  ///     canonical case-insensitive form, trailing separators trimmed; an
  ///     unresolvable path simply stays itself (uppercased, trimmed).</summary>
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
