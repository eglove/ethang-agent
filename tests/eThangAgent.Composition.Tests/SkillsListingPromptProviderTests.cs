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

  private sealed class FakeLearned(params SkillDefinition[] skills) : ILearnedSkillStore
  {
    private readonly SkillDefinition[] _skills = skills;
    public DomainError? ListFailing { get; init; }
    public int ListCalls { get; private set; }

    public Task<Result<IReadOnlyList<SkillDefinition>>> ListAsync(CancellationToken ct = default)
    {
      ListCalls++;
      return Task.FromResult(ListFailing is not null
          ? Result.Failure<IReadOnlyList<SkillDefinition>>(ListFailing)
          : Result.Success<IReadOnlyList<SkillDefinition>>([.. _skills]));
    }

    public Task<Result<SkillDefinition>> CreateAsync(SkillDefinition skill, CancellationToken ct = default) =>
        throw new NotSupportedException("unused");

    public Task<Result<SkillDefinition?>> GetAsync(string name, CancellationToken ct = default) =>
        throw new NotSupportedException("unused");

    public Task<Result<SkillDefinition>> UpdateAsync(SkillDefinition updated, CancellationToken ct = default) =>
        throw new NotSupportedException("unused");

    public Task<Result<bool>> DeleteAsync(string name, CancellationToken ct = default) =>
        throw new NotSupportedException("unused");

    public Task<Result<int>> AppendUsageAsync(string name, DateTimeOffset viewedAt, CancellationToken ct = default) =>
        throw new NotSupportedException("unused");
  }

  private static SkillDefinition BuiltIn(string name) => new(
      name, "desc " + name, "body " + name, 1, SkillSource.BuiltIn,
      null, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch);

  private static SkillDefinition File(string name, string origin, bool manual = false) => new(
      name, "desc " + name, "body " + name, 1, SkillSource.File,
      null, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, manual, origin);

  private static SkillDefinition Learned(string name) => new(
      name, "desc " + name, "body " + name, 1, SkillSource.Learned,
      "session-1", DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch);

  private static SkillsListingPromptProvider Provider(
      ISkillCatalog catalog,
      ILearnedSkillStore? learned = null,
      IReadOnlyList<SkillDirectory>? directories = null) => new(
          catalog,
          learned ?? new FakeLearned(),
          directories ?? []);

  // ---- render contract ----------------------------------------------------

  [Fact]
  public async Task Build_AllFourGroups_RendersHeadersAndEntries()
  {
    FakeCatalog catalog = new(
        BuiltIn("builtin-a"),
        File("global-skill", GlobalDir),
        File("ws-skill", WorkspaceDir));
    FakeLearned learned = new(Learned("learned-a"));

    string text = await Provider(catalog, learned,
        [new SkillDirectory(GlobalDir, SkillDirectoryScope.Global),
             new SkillDirectory(WorkspaceDir, SkillDirectoryScope.Workspace)]).BuildAsync();

    Assert.StartsWith("[skills listing — prefer a matching skill over improvising; load bodies with skill_view]", text, StringComparison.Ordinal);
    Assert.Contains("## Built-in\n- builtin-a: desc builtin-a", text, StringComparison.Ordinal);
    Assert.Contains("## Global directory skills\n- global-skill: desc global-skill", text, StringComparison.Ordinal);
    Assert.Contains("## Workspace directory skills\n- ws-skill: desc ws-skill", text, StringComparison.Ordinal);
    Assert.Contains("## Learned\n- learned-a: desc learned-a", text, StringComparison.Ordinal);
    Assert.DoesNotContain("[skills listing truncated", text, StringComparison.Ordinal);
  }

  [Fact]
  public async Task Build_ManualSkills_Excluded()
  {
    FakeCatalog catalog = new(
        BuiltIn("plain"),
        File("hidden-global", GlobalDir, manual: true),
        File("hidden-ws", WorkspaceDir, manual: true));
    FakeLearned learned = new(Learned("visible-learned"),
        new SkillDefinition("hidden-learned", "d", "b", 1, SkillSource.Learned,
            "session-1", DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, Manual: true));

    string text = await Provider(catalog, learned,
        [new SkillDirectory(GlobalDir, SkillDirectoryScope.Global),
             new SkillDirectory(WorkspaceDir, SkillDirectoryScope.Workspace)]).BuildAsync();

    Assert.DoesNotContain("hidden-global", text, StringComparison.Ordinal);
    Assert.DoesNotContain("hidden-ws", text, StringComparison.Ordinal);
    Assert.DoesNotContain("hidden-learned", text, StringComparison.Ordinal);
    Assert.Contains("- plain: desc plain", text, StringComparison.Ordinal);
    Assert.Contains("- visible-learned: desc visible-learned", text, StringComparison.Ordinal);
  }

  [Fact]
  public async Task Build_LongDescriptions_TruncatedAt60()
  {
    string longDescription = new('x', 80);
    SkillDefinition skill = BuiltIn("trunc") with { Description = longDescription };
    FakeCatalog catalog = new(skill);
    string text = await Provider(catalog).BuildAsync();

    Assert.Contains("- trunc: " + new string('x', 60) + '…', text, StringComparison.Ordinal);
    Assert.DoesNotContain(new string('x', 61), text, StringComparison.Ordinal);
  }

  [Fact]
  public async Task Build_CollisionLines_RenderVerbatim()
  {
    FakeCatalog catalog = new(
        ["[collision] alpha (global directory) shadowed by built-in",
            "[collision] beta (workspace directory) shadowed by global directory"],
        BuiltIn("alpha"),
        File("beta", GlobalDir));

    string text = await Provider(catalog, directories:
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
    Assert.DoesNotContain("## Learned", text, StringComparison.Ordinal);
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

  [Fact]
  public async Task Build_OverBudget_DescriptionsDropLowestPrecedenceFirst()
  {
    // 175 learned + 40 workspace + 40 global skills at ~26 chars per entry
    // (~10,900 chars) forces the description-stripping pass; stripping works
    // from the END of the lowest-precedence group (Learned), so the learned
    // TAIL goes bare while the learned HEAD keeps its description, and the
    // workspace/global/built-in groups keep their descriptions entirely.
    // No ENTRY drops at this level; per the never-silent rule the marker still
    // announces the dropped descriptions with e = 0.
    List<SkillDefinition> skills = [];
    for (int i = 0; 175 > i; i++)
    {
      skills.Add(Learned("learned-skill-" + i.ToString("d3", CultureInfo.InvariantCulture)));
    }

    for (int i = 0; 40 > i; i++)
    {
      skills.Add(File("ws-skill-" + i.ToString("d3", CultureInfo.InvariantCulture), WorkspaceDir));
    }

    for (int i = 0; 40 > i; i++)
    {
      skills.Add(File("global-skill-" + i.ToString("d3", CultureInfo.InvariantCulture), GlobalDir));
    }
    skills.Add(BuiltIn("the-builtin"));
    FakeCatalog catalog = new([.. skills.Where(s => s.Source != SkillSource.Learned)]);
    FakeLearned learned = new([.. skills.Where(s => s.Source == SkillSource.Learned)]);

    string text = await Provider(catalog, learned,
        directories: [new SkillDirectory(GlobalDir, SkillDirectoryScope.Global),
            new SkillDirectory(WorkspaceDir, SkillDirectoryScope.Workspace)]).BuildAsync();

    Assert.Matches("\\[skills listing truncated: showed \\d+ of \\d+ skills; dropped \\d+ descriptions and 0 entries — call skill_list for the full catalog\\]", text);
    // stripping starts at the END of the lowest-precedence group (Learned)...
    Assert.Contains("- learned-skill-173\n- learned-skill-174", text, StringComparison.Ordinal);
    // ...so the learned HEAD still shows its description...
    Assert.Contains("- learned-skill-000: desc learned-skill-000", text, StringComparison.Ordinal);
    // ...workspace and global tails keep their descriptions (entries never dropped)...
    Assert.Contains("- ws-skill-039: desc ws-skill-039", text, StringComparison.Ordinal);
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
    Assert.StartsWith("[skills listing — prefer a matching skill over improvising; load bodies with skill_view]", text, StringComparison.Ordinal);
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
    // 175 learned + 40 workspace + 40 global skills overflow to a strips-only
    // fixed point: descriptions drop until the block fits, no ENTRY ever drops,
    // and the marker must still announce the dropped descriptions (e = 0, d > 0).
    List<SkillDefinition> skills = [];
    for (int i = 0; 175 > i; i++)
    {
      skills.Add(Learned("learned-skill-" + i.ToString("d3", CultureInfo.InvariantCulture)));
    }

    for (int i = 0; 40 > i; i++)
    {
      skills.Add(File("ws-skill-" + i.ToString("d3", CultureInfo.InvariantCulture), WorkspaceDir));
    }

    for (int i = 0; 40 > i; i++)
    {
      skills.Add(File("global-skill-" + i.ToString("d3", CultureInfo.InvariantCulture), GlobalDir));
    }
    skills.Add(BuiltIn("the-builtin"));
    FakeCatalog catalog = new([.. skills.Where(s => s.Source != SkillSource.Learned)]);
    FakeLearned learned = new([.. skills.Where(s => s.Source == SkillSource.Learned)]);

    string text = await Provider(catalog, learned,
        directories: [new SkillDirectory(GlobalDir, SkillDirectoryScope.Global),
            new SkillDirectory(WorkspaceDir, SkillDirectoryScope.Workspace)]).BuildAsync();

    Assert.True(text.Length <= SkillListingBudget.MaxChars, "len=" + text.Length);
    // every entry still shown (strips-only), so N = M
    Assert.Matches("\\[skills listing truncated: showed \\d+ of \\d+ skills; dropped \\d+ descriptions and 0 entries — call skill_list for the full catalog\\]", text);
    // and at least one description was actually dropped
    Assert.Contains("- learned-skill-174\n", text, StringComparison.Ordinal);
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
    FakeLearned learned = new()
    {
      ListFailing = new DomainError("LearnedStoreUnavailable", "store offline"),
    };

    string text = await Provider(catalog, learned).BuildAsync();

    Assert.Contains("[warning] built-in skills unavailable: catalog exploded", text, StringComparison.Ordinal);
    Assert.Contains("[warning] learned skills unavailable: store offline", text, StringComparison.Ordinal);
    Assert.StartsWith("[skills listing — prefer a matching skill over improvising; load bodies with skill_view]", text, StringComparison.Ordinal);
    Assert.DoesNotContain("- keep:", text, StringComparison.Ordinal);
  }

  // ---- Learned-catalog dedup (spec #19 decision 2: presentation-layer merge) ----

  [Fact]
  public async Task Build_LearnedSkillShadowedByCatalog_SkippedWithCollisionLine()
  {
    FakeCatalog catalog = new(BuiltIn("alpha"), File("beta", GlobalDir));
    FakeLearned learned = new(Learned("beta"), Learned("learned-ok"));

    string text = await Provider(catalog, learned,
        [new SkillDirectory(GlobalDir, SkillDirectoryScope.Global)]).BuildAsync();

    Assert.Contains("## Learned\n- learned-ok: desc learned-ok", text, StringComparison.Ordinal);
    Assert.Contains("[collision] beta (learned) shadowed by global directory", text, StringComparison.Ordinal);
    // the shadowed learned row is gone entirely - not rendered twice
    Assert.DoesNotContain("desc beta\n- beta", text, StringComparison.Ordinal);
    int betaCount = text.Split("- beta:").Length - 1;
    Assert.Equal(1, betaCount);
  }

  [Fact]
  public async Task Build_LearnedSkillShadowedByBuiltIn_UsesBuiltInLabel()
  {
    FakeCatalog catalog = new(BuiltIn("alpha"));
    FakeLearned learned = new(Learned("alpha"));

    string text = await Provider(catalog, learned).BuildAsync();

    Assert.Contains("[collision] alpha (learned) shadowed by built-in", text, StringComparison.Ordinal);
    Assert.DoesNotContain("## Learned", text, StringComparison.Ordinal);
  }

  [Fact]
  public async Task Build_LearnedSkillShadowedByWorkspaceFile_UsesWorkspaceLabel()
  {
    FakeCatalog catalog = new(File("beta", WorkspaceDir));
    FakeLearned learned = new(Learned("beta"));

    string text = await Provider(catalog, learned,
        [new SkillDirectory(WorkspaceDir, SkillDirectoryScope.Workspace)]).BuildAsync();

    Assert.Contains("[collision] beta (learned) shadowed by workspace directory", text, StringComparison.Ordinal);
  }

  [Fact]
  public async Task Build_CatalogFailure_LearnedRendersWhole_NoDedupLines()
  {
    FakeCatalog catalog = new(BuiltIn("alpha"))
    {
      ListFailing = new DomainError("CatalogUnavailable", "catalog exploded"),
    };
    FakeLearned learned = new(Learned("alpha"));

    string text = await Provider(catalog, learned).BuildAsync();

    Assert.Contains("- alpha: desc alpha", text, StringComparison.Ordinal);
    Assert.DoesNotContain("[collision]", text, StringComparison.Ordinal);
    Assert.Contains("[warning] built-in skills unavailable: catalog exploded", text, StringComparison.Ordinal);
  }

  [Fact]
  public async Task Build_ManualLearnedSkipsDedupAnnouncement_WhenAlsoShadowed()
  {
    // A MANUAL learned skill never appears anywhere; its dedup line must not
    // render either (nothing dropped from a block it never belonged to).
    FakeCatalog catalog = new(BuiltIn("alpha"));
    FakeLearned learned = new(new SkillDefinition("alpha", "d", "b", 1, SkillSource.Learned,
        "session-1", DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, Manual: true));

    string text = await Provider(catalog, learned).BuildAsync();

    Assert.Contains("- alpha: desc alpha", text, StringComparison.Ordinal);
    Assert.DoesNotContain("[collision]", text, StringComparison.Ordinal);
  }

  [Fact]
  public async Task Build_Memoized_SecondCallDoesNotRehitTheFakes()
  {
    FakeCatalog catalog = new(BuiltIn("memo"));
    FakeLearned learned = new(Learned("memo-learned"));
    SkillsListingPromptProvider provider = Provider(catalog, learned);

    string first = await provider.BuildAsync();
    string second = await provider.BuildAsync();

    Assert.Equal(1, catalog.ListCalls);
    Assert.Equal(1, learned.ListCalls);
    Assert.Equal(first, second);
  }
}
