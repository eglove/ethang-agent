using System.Globalization;
using eThangAgent.SharedKernel;
using eThangAgent.SkillDomain;

namespace eThangAgent.Composition.Tests;

/// <summary>Contract cases for <see cref="SkillsListingPromptProvider" />: the exact
/// render contract (headers, entries, truncation, collision lines, truncation marker),
/// the deterministic budget overflow algorithm (descriptions strip lowest-precedence
/// group first, then whole entries drop in the same order; the marker always renders),
/// manual-skill exclusion, load-failure degradation, and per-instance memoization.</summary>
public class SkillsListingPromptProviderTests
{
  private const string GlobalDir = "C:\\skills\\global";
  private const string WorkspaceDir = "C:\\proj\\.agents\\skills";

  // ---- fakes -------------------------------------------------------------

  private sealed class FakeCatalog : ISkillCatalog, ISkillCatalogDiagnostics
  {
    private readonly SkillDefinition[] _skills;
    private readonly string[] _diagnostics;
    public DomainError? ListFailing { get; init; }
    public int ListCalls { get; private set; }

    public FakeCatalog(params SkillDefinition[] skills)
    {
      _skills = skills;
      _diagnostics = [];
    }

    public FakeCatalog(string[] diagnostics, params SkillDefinition[] skills)
    {
      _skills = skills;
      _diagnostics = diagnostics;
    }

    public Task<Result<IReadOnlyList<SkillDefinition>>> ListAsync(CancellationToken ct = default)
    {
      ListCalls++;
      return Task.FromResult(ListFailing is not null
          ? Result.Failure<IReadOnlyList<SkillDefinition>>(ListFailing)
          : Result.Success<IReadOnlyList<SkillDefinition>>([.. _skills]));
    }

    public Task<Result<SkillDefinition>> GetAsync(string name, CancellationToken ct = default) =>
        Task.FromResult(Result.Failure<SkillDefinition>(new DomainError("SkillNotFound", "unused")));

    public Task<Result<IReadOnlyList<string>>> GetDiagnosticsAsync(CancellationToken ct = default) =>
        Task.FromResult(Result.Success<IReadOnlyList<string>>(_diagnostics));
  }

  private static SkillDefinition BuiltIn(string name) => new(
      name, "desc " + name, "body " + name, 1, SkillSource.BuiltIn);

  private static SkillDefinition File(string name, string origin, bool manual = false) => new(
      name, "desc " + name, "body " + name, 1, SkillSource.File, manual, origin);

  private static SkillsListingPromptProvider Provider(
      ISkillCatalog catalog,
      IReadOnlyList<SkillDirectory>? directories = null) => new(
          catalog,
          directories ?? []);

  // ---- render contract ----------------------------------------------------

  [Fact]
  public async Task Build_AllThreeGroups_RendersHeadersAndEntries()
  {
    FakeCatalog catalog = new(
        BuiltIn("builtin-a"),
        File("global-skill", GlobalDir),
        File("ws-skill", WorkspaceDir));

    string text = await Provider(catalog,
        [new SkillDirectory(GlobalDir, SkillDirectoryScope.Global),
             new SkillDirectory(WorkspaceDir, SkillDirectoryScope.Workspace)]).BuildAsync();

    Assert.StartsWith("[skills listing — prefer a matching skill over improvising; load bodies with skill_view (an exec-bridge call: Tools.Invoke(\"skill_view\", new { name = \"<name>\" }) inside exec)]", text, StringComparison.Ordinal);
    Assert.Contains("## Built-in\n- builtin-a: desc builtin-a", text, StringComparison.Ordinal);
    Assert.Contains("## Global directory skills\n- global-skill: desc global-skill", text, StringComparison.Ordinal);
    Assert.Contains("## Workspace directory skills\n- ws-skill: desc ws-skill", text, StringComparison.Ordinal);
    Assert.DoesNotContain("[skills listing truncated", text, StringComparison.Ordinal);
  }

  [Fact]
  public async Task Build_ManualSkills_Excluded()
  {
    FakeCatalog catalog = new(
        BuiltIn("plain"),
        File("hidden-global", GlobalDir, manual: true),
        File("hidden-ws", WorkspaceDir, manual: true),
        File("visible-ws", WorkspaceDir));

    string text = await Provider(catalog,
        [new SkillDirectory(GlobalDir, SkillDirectoryScope.Global),
             new SkillDirectory(WorkspaceDir, SkillDirectoryScope.Workspace)]).BuildAsync();

    Assert.DoesNotContain("hidden-global", text, StringComparison.Ordinal);
    Assert.DoesNotContain("hidden-ws", text, StringComparison.Ordinal);
    Assert.Contains("- plain: desc plain", text, StringComparison.Ordinal);
    Assert.Contains("- visible-ws: desc visible-ws", text, StringComparison.Ordinal);
  }

  [Fact]
  public async Task Build_LongDescriptions_TruncatedAtDescriptionLimit()
  {
    int limit = SkillListingBudget.DescriptionLimit;
    string longDescription = new('x', limit + 20);
    SkillDefinition skill = BuiltIn("trunc") with { Description = longDescription };
    FakeCatalog catalog = new(skill);
    string text = await Provider(catalog).BuildAsync();

    Assert.Contains("- trunc: " + new string('x', limit) + '…', text, StringComparison.Ordinal);
    Assert.DoesNotContain(new string('x', limit + 1), text, StringComparison.Ordinal);
  }

  [Fact]
  public async Task Build_RealisticDescription_RendersWhole_TriggerClauseSurvives()
  {
    // THE REGRESSION THIS CAP EXISTS FOR: every real SKILL.md description states its
    // trigger as a trailing 'Use when ...' clause, and the clause is what a model matches
    // a task against. A cap that fires on ordinary descriptions cuts exactly that clause
    // off - silently, since it happens before the budget pass and so emits no marker.
    const string Trigger = "Use when facing two or more independent tasks with no shared state.";
    string description = "Fan out independent subtasks to parallel subagents and fan in their results. " + Trigger;
    FakeCatalog catalog = new(BuiltIn("dispatching-parallel-agents") with { Description = description });

    string text = await Provider(catalog).BuildAsync();

    Assert.Contains(Trigger, text, StringComparison.Ordinal);
    Assert.DoesNotContain('…', text);
  }

  [Fact]
  public async Task Build_CollisionLines_RenderVerbatim()
  {
    FakeCatalog catalog = new(
        ["[collision] alpha (global directory) shadowed by built-in",
            "[collision] beta (workspace directory) shadowed by global directory"],
        BuiltIn("alpha"),
        File("beta", GlobalDir));

    string text = await Provider(catalog,
        [new SkillDirectory(GlobalDir, SkillDirectoryScope.Global)]).BuildAsync();

    Assert.Contains("[collision] alpha (global directory) shadowed by built-in", text, StringComparison.Ordinal);
    Assert.Contains("[collision] beta (workspace directory) shadowed by global directory", text, StringComparison.Ordinal);
    Assert.DoesNotContain("[warning] [collision]", text, StringComparison.Ordinal);
  }

  [Fact]
  public async Task Build_EmptyGroups_NoHeader()
  {
    FakeCatalog catalog = new(BuiltIn("only-builtin"));

    string text = await Provider(catalog).BuildAsync();

    Assert.Contains("## Built-in", text, StringComparison.Ordinal);
    Assert.DoesNotContain("## Global directory skills", text, StringComparison.Ordinal);
    Assert.DoesNotContain("## Workspace directory skills", text, StringComparison.Ordinal);
  }

  [Fact]
  public async Task Build_SmallCatalog_FitsBudget_NoMarker()
  {
    List<SkillDefinition> skills = [];
    for (int i = 0; 20 > i; i++)
    {
      skills.Add(File("file-skill-" + i.ToString("d2", CultureInfo.InvariantCulture), GlobalDir));
    }

    string text = await Provider(new FakeCatalog([.. skills]),
        directories: [new SkillDirectory(GlobalDir, SkillDirectoryScope.Global)]).BuildAsync();

    Assert.True(text.Length <= SkillListingBudget.MaxChars);
    Assert.DoesNotContain("[skills listing truncated", text, StringComparison.Ordinal);
    Assert.Contains("file-skill-00", text, StringComparison.Ordinal);
    Assert.Contains("file-skill-19", text, StringComparison.Ordinal);
  }

  /// <summary>Workspace entries for the overflow fixtures: enough of them that the
  ///     fully-described render exceeds the budget while the fully-stripped render fits,
  ///     so the pass under test is the description strip and not the entry drop.</summary>
  private const int WorkspaceOverflowCount = 260;

  [Fact]
  public async Task Build_OverBudget_DescriptionsDropLowestPrecedenceFirst()
  {
    // 260 workspace + 40 global skills at ~30 chars per entry forces the
    // description-stripping pass; stripping works from the END of the
    // lowest-precedence group (Workspace), so the workspace TAIL goes bare while
    // the workspace HEAD keeps its description, and the global/built-in groups
    // keep their descriptions entirely. No ENTRY drops at this level; per the
    // never-silent rule the marker still announces the dropped descriptions with
    // e = 0.
    List<SkillDefinition> skills = [];
    for (int i = 0; WorkspaceOverflowCount > i; i++)
    {
      skills.Add(File("ws-skill-" + i.ToString("d3", CultureInfo.InvariantCulture), WorkspaceDir));
    }

    for (int i = 0; 40 > i; i++)
    {
      skills.Add(File("global-skill-" + i.ToString("d3", CultureInfo.InvariantCulture), GlobalDir));
    }
    skills.Add(BuiltIn("the-builtin"));
    FakeCatalog catalog = new([.. skills]);

    string text = await Provider(catalog,
        directories: [new SkillDirectory(GlobalDir, SkillDirectoryScope.Global),
            new SkillDirectory(WorkspaceDir, SkillDirectoryScope.Workspace)]).BuildAsync();

    Assert.Matches("\\[skills listing truncated: showed \\d+ of \\d+ skills; dropped \\d+ descriptions and 0 entries — call skill_list for the full catalog\\]", text);
    // stripping starts at the END of the lowest-precedence group (Workspace)...
    Assert.Contains("- ws-skill-258\n- ws-skill-259", text, StringComparison.Ordinal);
    // ...so the workspace HEAD still shows its description...
    Assert.Contains("- ws-skill-000: desc ws-skill-000", text, StringComparison.Ordinal);
    // ...the global tail keeps its description (entries never dropped)...
    Assert.Contains("- global-skill-039: desc global-skill-039", text, StringComparison.Ordinal);
    // ...and the built-in keeps its description entirely.
    Assert.Contains("- the-builtin: desc the-builtin", text, StringComparison.Ordinal);
  }

  [Fact]
  public async Task Build_ExtremeOverflow_EntriesDropAndMarkerAlwaysRenders()
  {
    // 800 entries at ~25 chars (~21,700 chars) forces whole-entry drops; the
    // marker must survive with the final shown/dropped accounting.
    const int Count = 800;
    List<SkillDefinition> skills = [];
    for (int i = 0; Count > i; i++)
    {
      skills.Add(File("x-skill-" + i.ToString("d3", CultureInfo.InvariantCulture), GlobalDir));
    }
    FakeCatalog catalog = new([.. skills]);

    string text = await Provider(catalog,
        directories: [new SkillDirectory(GlobalDir, SkillDirectoryScope.Global)]).BuildAsync();

    Assert.True(text.Length <= SkillListingBudget.MaxChars,
        "block exceeds MaxChars: " + text.Length);
    Assert.StartsWith("[skills listing — prefer a matching skill over improvising; load bodies with skill_view (an exec-bridge call: Tools.Invoke(\"skill_view\", new { name = \"<name>\" }) inside exec)]", text, StringComparison.Ordinal);
    int markerIndex = text.IndexOf("[skills listing truncated", StringComparison.Ordinal);
    Assert.True(markerIndex > 0, "truncation marker must render");
    Assert.EndsWith("]", text.TrimEnd(), StringComparison.Ordinal);
    Assert.Matches("\\[skills listing truncated: showed \\d+ of 800 skills; dropped \\d+ descriptions and \\d+ entries — call skill_list for the full catalog\\]", text[markerIndex..]);
    // early entries survive; the block ends at the marker
    Assert.Contains("x-skill-000", text, StringComparison.Ordinal);
  }

  [Fact]
  public async Task Build_EmptyCatalog_RendersEmptyString()
  {
    FakeCatalog catalog = new();

    string text = await Provider(catalog).BuildAsync();

    Assert.Equal(string.Empty, text);
  }

  [Fact]
  public async Task Build_StripsOnlyOverflow_MarkerAnnouncesDroppedDescriptions()
  {
    // 260 workspace + 40 global skills overflow to a strips-only fixed point:
    // descriptions drop until the block fits, no ENTRY ever drops, and the marker
    // must still announce the dropped descriptions (e = 0, d > 0).
    List<SkillDefinition> skills = [];
    for (int i = 0; WorkspaceOverflowCount > i; i++)
    {
      skills.Add(File("ws-skill-" + i.ToString("d3", CultureInfo.InvariantCulture), WorkspaceDir));
    }

    for (int i = 0; 40 > i; i++)
    {
      skills.Add(File("global-skill-" + i.ToString("d3", CultureInfo.InvariantCulture), GlobalDir));
    }
    skills.Add(BuiltIn("the-builtin"));
    FakeCatalog catalog = new([.. skills]);

    string text = await Provider(catalog,
        directories: [new SkillDirectory(GlobalDir, SkillDirectoryScope.Global),
            new SkillDirectory(WorkspaceDir, SkillDirectoryScope.Workspace)]).BuildAsync();

    Assert.True(text.Length <= SkillListingBudget.MaxChars, "len=" + text.Length);
    // every entry still shown (strips-only), so N = M
    Assert.Matches("\\[skills listing truncated: showed \\d+ of \\d+ skills; dropped \\d+ descriptions and 0 entries — call skill_list for the full catalog\\]", text);
    // and at least one description was actually dropped
    Assert.Contains("- ws-skill-259\n", text, StringComparison.Ordinal);
  }

  [Fact]
  public async Task Build_NonCollisionDiagnostics_RenderAsWarningLines()
  {
    // The composite catalog never fails ListAsync: directory-load failures and
    // built-in-catalog failures surface ONLY as non-collision diagnostic lines.
    // The listing must announce them as '[warning] <text>' so a silently missing
    // directory is never invisible. Collision lines stay verbatim (no double prefix).
    FakeCatalog catalog = new(
        ["directory load failed C:\\broken: DirectoryReadFailed",
            "[collision] alpha (workspace directory) shadowed by global directory"],
        BuiltIn("alpha"),
        File("beta", GlobalDir));

    string text = await Provider(catalog, directories:
        [new SkillDirectory(GlobalDir, SkillDirectoryScope.Global)]).BuildAsync();

    Assert.Contains("[warning] directory load failed C:\\broken: DirectoryReadFailed", text, StringComparison.Ordinal);
    Assert.Contains("[collision] alpha (workspace directory) shadowed by global directory", text, StringComparison.Ordinal);
    Assert.DoesNotContain("[warning] [collision]", text, StringComparison.Ordinal);
    Assert.DoesNotContain("[warning] catalog diagnostics unavailable", text, StringComparison.Ordinal);
  }

  [Fact]
  public async Task Build_LoadFailure_RendersWarningLine()
  {
    FakeCatalog catalog = new(BuiltIn("keep"))
    {
      ListFailing = new DomainError("CatalogUnavailable", "catalog exploded"),
    };

    string text = await Provider(catalog).BuildAsync();

    Assert.Contains("[warning] built-in skills unavailable: catalog exploded", text, StringComparison.Ordinal);
    Assert.StartsWith("[skills listing — prefer a matching skill over improvising; load bodies with skill_view (an exec-bridge call: Tools.Invoke(\"skill_view\", new { name = \"<name>\" }) inside exec)]", text, StringComparison.Ordinal);
    Assert.DoesNotContain("- keep:", text, StringComparison.Ordinal);
  }

  [Fact]
  public async Task Build_Memoized_SecondCallDoesNotRehitTheFakes()
  {
    FakeCatalog catalog = new(BuiltIn("memo"));
    SkillsListingPromptProvider provider = Provider(catalog);

    string first = await provider.BuildAsync();
    string second = await provider.BuildAsync();

    Assert.Equal(1, catalog.ListCalls);
    Assert.Equal(first, second);
  }
}
