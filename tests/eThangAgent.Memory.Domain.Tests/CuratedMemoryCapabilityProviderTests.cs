using eThangAgent.CapabilityDomain;
using eThangAgent.SharedKernel;

namespace eThangAgent.MemoryDomain.Tests;

public class CuratedMemoryCapabilityProviderTests
{
  private static readonly DateTimeOffset FixedNow = new(2026, 8, 21, 12, 0, 0, TimeSpan.Zero);
  private const string Workspace = "ws-abc-123";
  private const string AmbientSession = "session-xyz";
  private static readonly Guid KnownId = Guid.Parse("3f2a9f0e-1111-2222-3333-444455556666");
  private static readonly string KnownFullId = KnownId.ToString();

  private sealed class Harness
  {
    public FakeCuratedMemoryStore Store { get; } = new();
    public int BumpCount { get; private set; }
    public string WorkspaceValue { get; set; } = Workspace;
    public string? AmbientSessionValue { get; set; } = AmbientSession;
    public DateTimeOffset ClockValue { get; set; } = FixedNow;
    public int ProvenanceCalls { get; private set; }

    public CuratedMemoryCapabilityProvider Provider() => new(
        Store,
        () => WorkspaceValue,
        Provenance,
        Bump,
        () => ClockValue);

    private string? Provenance()
    {
      ProvenanceCalls++;
      return AmbientSessionValue;
    }

    private int Bump()
    {
      BumpCount++;
      return BumpCount;
    }
  }

  private static CuratedMemory Row(
      Guid? id = null,
      string workspaceId = Workspace,
      MemoryCategory category = MemoryCategory.Insight,
      IReadOnlyList<string>? tags = null,
      string content = "seed content",
      string? usageHint = null,
      MemoryScope scope = MemoryScope.Workspace,
      string? provenance = null,
      int version = 1) => new(
      id ?? Guid.NewGuid(), workspaceId, category, tags ?? [], content, usageHint,
      scope, provenance, version, FixedNow, FixedNow);

  // ---- provider shape ----

  [Fact]
  public void Provider_ExposesFourActions_UnderMemoriesId()
  {
    CuratedMemoryCapabilityProvider provider = new Harness().Provider();

    Assert.Equal("memories", provider.Id);
    Assert.Equal(["search", "add", "update", "remove"], provider.Actions.Select(a => a.Name));
  }

  // ---- search ----

  [Fact]
  public async Task Search_ZeroHits_ExactSingleLine()
  {
    CapabilityInvocationResult result = await new Harness().Provider().InvokeAsync("search", "{}", ct: TestContext.Current.CancellationToken);

    Assert.False(result.IsError);
    Assert.Equal("[memories] 0 hit(s)", result.Content);
  }

  [Fact]
  public async Task Search_RendersHeader_Row_AndHintLines_Exactly()
  {
    Harness h = new();
    h.Store._rows[KnownId] = Row(
        id: KnownId, scope: MemoryScope.Global, workspaceId: "",
        category: MemoryCategory.Preference, tags: ["api", "sql"],
        content: "Prefer explicit over implicit.", usageHint: "Cite in reviews.");

    CapabilityInvocationResult result = await h.Provider().InvokeAsync("search", "{}", ct: TestContext.Current.CancellationToken);

    Assert.False(result.IsError);
    Assert.Equal(
        "[memories] 1 hit(s)\n" +
        $"[mem] id={KnownFullId} v1 cat=preference scope=global tags=api,sql :: Prefer explicit over implicit.\n" +
        "     hint: Cite in reviews.",
        result.Content);
  }

  [Fact]
  public async Task Search_Defaults_ForwardWorkspaceAndLimit20()
  {
    Harness h = new();

    CapabilityInvocationResult result = await h.Provider().InvokeAsync("search", "{}", ct: TestContext.Current.CancellationToken);

    Assert.False(result.IsError);
    Assert.Equal(Workspace, h.Store._lastSearchWorkspaceId);
    Assert.Null(h.Store._lastSearchQuery);
    Assert.Null(h.Store._lastSearchCategory);
    Assert.Equal([], h.Store._lastSearchTags); // absent array forwards [], the store's "no constraint"
    Assert.Equal(20, h.Store._lastSearchLimit);
  }

  [Fact]
  public async Task Search_AllParameters_ParsedAndForwarded()
  {
    Harness h = new();

    CapabilityInvocationResult result = await h.Provider().InvokeAsync("search",
                             /*lang=json,strict*/
                             """{"query":"ftx","category":"failure","tags":["api","sql"],"scope":"workspace","limit":5}""", ct: TestContext.Current.CancellationToken);

    Assert.False(result.IsError);
    Assert.Equal("ftx", h.Store._lastSearchQuery);
    Assert.Equal(MemoryCategory.Failure, h.Store._lastSearchCategory);
    Assert.NotNull(h.Store._lastSearchTags);
    Assert.Equal(["api", "sql"], h.Store._lastSearchTags);
    Assert.Equal(5, h.Store._lastSearchLimit);
  }

  [Fact]
  public async Task Search_TagElementsValidated_InvalidTagFailsBeforeStoreConsult()
  {
    Harness h = new();

    CapabilityInvocationResult result = await h.Provider().InvokeAsync("search",
                             /*lang=json,strict*/
                             """{"tags":["SQL"]}""", ct: TestContext.Current.CancellationToken);

    Assert.True(result.IsError);
    Assert.StartsWith("Error [InvalidTag]:", result.Content, StringComparison.Ordinal);
    Assert.Contains("^[a-z0-9][a-z0-9-_]{0,31}$", result.Content, StringComparison.Ordinal);
    Assert.Null(h.Store._lastSearchTags); // rejected before the store was consulted
  }

  [Fact]
  public async Task Search_TruncatesContentAt120_AndHintAt80_WithoutMarkers()
  {
    Harness h = new();
    string longContent = new('x', 300);
    string longHint = new('y', 150);
    h.Store._rows[KnownId] = Row(id: KnownId, content: longContent, usageHint: longHint);

    CapabilityInvocationResult result = await h.Provider().InvokeAsync("search", "{}", ct: TestContext.Current.CancellationToken);

    string[] lines = result.Content.Split('\n');
    Assert.Equal(3, lines.Length);
    Assert.Equal("[memories] 1 hit(s)", lines[0]);
    Assert.Equal(
        $"[mem] id={KnownFullId} v1 cat=insight scope=workspace tags= :: {new string('x', 120)}",
        lines[1]);
    Assert.Equal($"     hint: {new string('y', 80)}", lines[2]);
  }

  [Fact]
  public async Task Search_LimitOvershoot_ClampsTo100_WithVisibleWarningLine()
  {
    Harness h = new();

    CapabilityInvocationResult result = await h.Provider().InvokeAsync("search", /*lang=json,strict*/ """{"limit":250}""", ct: TestContext.Current.CancellationToken);

    Assert.False(result.IsError);
    Assert.Equal(100, h.Store._lastSearchLimit);
    Assert.EndsWith("[warning] limit clamped to 100", result.Content, StringComparison.Ordinal);
  }

  [Theory]
  [InlineData(0)]
  [InlineData(-5)]
  public async Task Search_LimitBelowOne_RejectedWithInvalidLimit(int limit)
  {
    Harness h = new();

    CapabilityInvocationResult result = await h.Provider().InvokeAsync("search", $$"""{"limit":{{limit}}}""", ct: TestContext.Current.CancellationToken);

    Assert.True(result.IsError);
    Assert.Equal("Error [InvalidLimit]: 'limit' must be an integer >= 1.", result.Content);
    Assert.Null(h.Store._lastSearchWorkspaceId); // rejected before the store was ever consulted
  }

  [Fact]
  public async Task Search_Category_IsExactLowercaseOnly()
  {
    CapabilityInvocationResult result = await new Harness().Provider().InvokeAsync("search", /*lang=json,strict*/ """{"category":"Insight"}""", ct: TestContext.Current.CancellationToken);

    Assert.True(result.IsError);
    Assert.StartsWith("Error [InvalidCategory]:", result.Content, StringComparison.Ordinal);
    foreach (string name in (string[])["convention", "preference", "insight", "failure", "reference"])
    {
      Assert.Contains(name, result.Content, StringComparison.Ordinal);
    }
  }

  [Fact]
  public async Task Search_Scope_IsExactLowercaseOnly()
  {
    CapabilityInvocationResult result = await new Harness().Provider().InvokeAsync("search", /*lang=json,strict*/ """{"scope":"Galaxy"}""", ct: TestContext.Current.CancellationToken);

    Assert.True(result.IsError);
    Assert.StartsWith("Error [InvalidScope]:", result.Content, StringComparison.Ordinal);
    Assert.Contains("workspace | global", result.Content, StringComparison.Ordinal);
  }

  [Fact]
  public async Task Search_ScopeFilter_NarrowsVisibleRows()
  {
    Harness h = new();
    CuratedMemory wsRow = Row(content: "ws note", scope: MemoryScope.Workspace, workspaceId: Workspace);
    CuratedMemory globalRow = Row(content: "global note", scope: MemoryScope.Global, workspaceId: "");
    h.Store._rows[wsRow.Id] = wsRow;
    h.Store._rows[globalRow.Id] = globalRow;

    CapabilityInvocationResult anyScope = await h.Provider().InvokeAsync("search", "{}", ct: TestContext.Current.CancellationToken);
    CapabilityInvocationResult workspaceOnly = await h.Provider().InvokeAsync("search", /*lang=json,strict*/ """{"scope":"workspace"}""", ct: TestContext.Current.CancellationToken);
    CapabilityInvocationResult globalOnly = await h.Provider().InvokeAsync("search", /*lang=json,strict*/ """{"scope":"global"}""", ct: TestContext.Current.CancellationToken);

    Assert.Contains("ws note", anyScope.Content, StringComparison.Ordinal);
    Assert.Contains("global note", anyScope.Content, StringComparison.Ordinal);
    Assert.Contains("ws note", workspaceOnly.Content, StringComparison.Ordinal);
    Assert.DoesNotContain("global note", workspaceOnly.Content, StringComparison.Ordinal);
    Assert.Contains("global note", globalOnly.Content, StringComparison.Ordinal);
    Assert.DoesNotContain("ws note", globalOnly.Content, StringComparison.Ordinal);
  }

  // ---- add ----

  [Fact]
  public async Task Add_HappyPath_WorkspaceScope_BuildsFullRecord_BumpsOnce()
  {
    Harness h = new();

    CapabilityInvocationResult result = await h.Provider().InvokeAsync("add",
                             /*lang=json,strict*/
                             """{"content":"  Deploy via winget.  ","category":"convention","tags":["deploy","winget","deploy"],"usage_hint":"Check before releases","scope":"workspace"}""", ct: TestContext.Current.CancellationToken);

    Assert.False(result.IsError);
    Assert.Equal(1, h.Store._addCallCount);
    CuratedMemory stored = h.Store._rows.Values.Single();
    Assert.Equal(
        $"[memories] added {stored.Id} v1 (cat=convention scope=workspace)",
        result.Content);

    Assert.Equal("Deploy via winget.", stored.Content); // trimmed
    Assert.Equal(MemoryCategory.Convention, stored.Category);
    Assert.Equal(["deploy", "winget"], stored.Tags); // deduplicated, first-seen order
    Assert.Equal("Check before releases", stored.UsageHint);
    Assert.Equal(MemoryScope.Workspace, stored.Scope);
    Assert.Equal(Workspace, stored.WorkspaceId); // keyed by the service's injected workspace id
    Assert.Equal(1, stored.Version);
    Assert.Equal(FixedNow, stored.CreatedAt);
    Assert.Equal(FixedNow, stored.UpdatedAt);
    Assert.Equal(AmbientSession, stored.ProvenanceSession); // ambient, captured via accessor
    Assert.Equal(1, h.ProvenanceCalls);
    Assert.Equal(1, h.BumpCount); // bumped exactly once per successful add
  }

  [Fact]
  public async Task Add_GlobalScope_EmptyWorkspaceKey()
  {
    Harness h = new();

    CapabilityInvocationResult result = await h.Provider().InvokeAsync("add",
                             /*lang=json,strict*/
                             """{"content":"Pin .NET 10 SDK.","category":"reference","scope":"global"}""", ct: TestContext.Current.CancellationToken);

    Assert.False(result.IsError);
    CuratedMemory stored = h.Store._rows.Values.Single();
    Assert.Equal("", stored.WorkspaceId); // empty string ⇒ Global scope row
    Assert.Equal(MemoryScope.Global, stored.Scope);
    Assert.Contains("(cat=reference scope=global)", result.Content, StringComparison.Ordinal);
    Assert.Equal([], stored.Tags);
    Assert.Null(stored.UsageHint);
    Assert.Equal(1, h.BumpCount);
  }

  [Theory]
  [InlineData(/*lang=json,strict*/ """{"category":"insight","scope":"workspace"}""")]
  [InlineData(/*lang=json,strict*/ """{"content":"","category":"insight","scope":"workspace"}""")]
  [InlineData(/*lang=json,strict*/ """{"content":"   ","category":"insight","scope":"workspace"}""")]
  public async Task Add_Content_RequiredNonEmptyAfterTrim(string json)
  {
    Harness h = new();

    CapabilityInvocationResult result = await h.Provider().InvokeAsync("add", json, ct: TestContext.Current.CancellationToken);

    Assert.StartsWith("Error [MissingContent]:", result.Content, StringComparison.Ordinal);
    Assert.Equal(0, h.Store._addCallCount);
    Assert.Equal(0, h.BumpCount); // failed validation never bumps the counter
  }

  [Fact]
  public async Task Add_ContentOver4000_NamesLimitAndActual()
  {
    Harness h = new();
    string json = """{"content":""" + "\"" + new string('a', 4001) + "\""
               + ""","category":"insight","scope":"global"}""";

    CapabilityInvocationResult result = await h.Provider().InvokeAsync("add", json, ct: TestContext.Current.CancellationToken);

    Assert.True(result.IsError);
    Assert.StartsWith("Error [ContentTooLong]:", result.Content, StringComparison.Ordinal);
    Assert.Contains("4000", result.Content, StringComparison.Ordinal);
    Assert.Contains("4001", result.Content, StringComparison.Ordinal);
    Assert.Equal(0, h.BumpCount);
  }

  [Fact]
  public async Task Add_MissingCategory_FailsBeforeStore()
  {
    Harness h = new();

    CapabilityInvocationResult result = await h.Provider().InvokeAsync("add",
                             /*lang=json,strict*/
                             """{"content":"note","scope":"workspace"}""", ct: TestContext.Current.CancellationToken);

    Assert.StartsWith("Error [MissingCategory]:", result.Content, StringComparison.Ordinal);
    Assert.Equal(0, h.Store._addCallCount);
    Assert.Equal(0, h.BumpCount);
  }

  [Fact]
  public async Task Add_InvalidCategory_ListsAllFiveCategories()
  {
    CapabilityInvocationResult result = await new Harness().Provider().InvokeAsync("add",
                             /*lang=json,strict*/
                             """{"content":"note","category":"Curated","scope":"workspace"}""", ct: TestContext.Current.CancellationToken);

    Assert.True(result.IsError);
    Assert.StartsWith("Error [InvalidCategory]:", result.Content, StringComparison.Ordinal);
    foreach (string name in (string[])["convention", "preference", "insight", "failure", "reference"])
    {
      Assert.Contains(name, result.Content, StringComparison.Ordinal);
    }
  }

  [Fact]
  public async Task Add_TooManyTags_NamesLimitAndActual()
  {
    IEnumerable<string> tags = Enumerable.Range(1, 13).Select(i => $"tag{i}");
    string json = """{"content":"note","category":"insight","scope":"global","tags":["""
               + string.Join(",", tags.Select(t => $"\"{t}\"")) + "]}";

    CapabilityInvocationResult result = await new Harness().Provider().InvokeAsync("add", json, ct: TestContext.Current.CancellationToken);

    Assert.True(result.IsError);
    Assert.StartsWith("Error [TooManyTags]:", result.Content, StringComparison.Ordinal);
    Assert.Contains("12", result.Content, StringComparison.Ordinal);
    Assert.Contains("13", result.Content, StringComparison.Ordinal);
  }

  [Fact]
  public async Task Add_InvalidTag_QuotesTheRule()
  {
    CapabilityInvocationResult result = await new Harness().Provider().InvokeAsync("add",
                             /*lang=json,strict*/
                             """{"content":"note","category":"insight","scope":"global","tags":["Bad Tag"]}""", ct: TestContext.Current.CancellationToken);

    Assert.True(result.IsError);
    Assert.StartsWith("Error [InvalidTag]:", result.Content, StringComparison.Ordinal);
    Assert.Contains("Bad Tag", result.Content, StringComparison.Ordinal);
    Assert.Contains("^[a-z0-9][a-z0-9-_]{0,31}$", result.Content, StringComparison.Ordinal);
  }

  [Fact]
  public async Task Add_HintTooLong_NamesLimit()
  {
    string json = """{"content":"note","category":"insight","scope":"global","usage_hint":"""
               + "\"" + new string('h', 201) + "\"}";

    CapabilityInvocationResult result = await new Harness().Provider().InvokeAsync("add", json, ct: TestContext.Current.CancellationToken);

    Assert.True(result.IsError);
    Assert.StartsWith("Error [HintTooLong]:", result.Content, StringComparison.Ordinal);
    Assert.Contains("200", result.Content, StringComparison.Ordinal);
  }

  [Fact]
  public async Task Add_MissingScope_Fails()
  {
    CapabilityInvocationResult result = await new Harness().Provider().InvokeAsync("add",
                             /*lang=json,strict*/
                             """{"content":"note","category":"insight"}""", ct: TestContext.Current.CancellationToken);

    Assert.StartsWith("Error [MissingScope]:", result.Content, StringComparison.Ordinal);
  }

  [Fact]
  public async Task Add_Scope_IsExactLowercaseOnly()
  {
    CapabilityInvocationResult result = await new Harness().Provider().InvokeAsync("add",
                             /*lang=json,strict*/
                             """{"content":"note","category":"insight","scope":"Workspace"}""", ct: TestContext.Current.CancellationToken);

    Assert.True(result.IsError);
    Assert.StartsWith("Error [InvalidScope]:", result.Content, StringComparison.Ordinal);
    Assert.Contains("workspace | global", result.Content, StringComparison.Ordinal);
  }

  [Fact]
  public async Task Add_SessionParameter_Rejected_ProvenanceStaysAmbient()
  {
    Harness h = new();

    CapabilityInvocationResult result = await h.Provider().InvokeAsync("add",
                             /*lang=json,strict*/
                             """{"content":"note","category":"insight","scope":"global","session":"forged-id"}""", ct: TestContext.Current.CancellationToken);

    Assert.True(result.IsError);
    Assert.StartsWith("Error [InvalidActionInput]:", result.Content, StringComparison.Ordinal);
    Assert.Contains("Unknown parameter 'session'", result.Content, StringComparison.Ordinal);
    Assert.Equal(0, h.Store._addCallCount);
    Assert.Equal(0, h.BumpCount);
  }

  [Fact]
  public async Task Add_StoreFailure_SurfacesTypedError_NeverBumps()
  {
    Harness h = new();
    h.Store._addResultOverride = Result.Failure<CuratedMemory>(
        new DomainError(CuratedMemoryErrors.StorageError, "disk unavailable"));

    CapabilityInvocationResult result = await h.Provider().InvokeAsync("add",
                             /*lang=json,strict*/
                             """{"content":"note","category":"insight","scope":"global"}""", ct: TestContext.Current.CancellationToken);

    Assert.True(result.IsError);
    Assert.Equal("Error [StorageError]: disk unavailable", result.Content);
    Assert.Equal(0, h.BumpCount);
  }

  // ---- update ----

  [Fact]
  public async Task Update_HappyPath_AppliesDeltas_BumpsVersion_UsesClock_NeverBumpsCounter()
  {
    Harness h = new();
    h.Store._rows[KnownId] = Row(
        id: KnownId, version: 2, content: "old body", tags: ["legacy"],
        category: MemoryCategory.Insight, usageHint: "keep me", provenance: "orig-session");
    h.ClockValue = FixedNow.AddMinutes(5);

    CapabilityInvocationResult result = await h.Provider().InvokeAsync("update",
        $$"""{"id":"{{KnownId}}","expected_version":2,"content":"new body","tags":["fresh"]}""", ct: TestContext.Current.CancellationToken);

    Assert.False(result.IsError);
    Assert.Equal($"[memories] updated {KnownFullId} v3", result.Content);
    CuratedMemory stored = h.Store._rows[KnownId];
    Assert.Equal(3, stored.Version);
    Assert.Equal("new body", stored.Content);
    Assert.Equal(["fresh"], stored.Tags);
    Assert.Equal(MemoryCategory.Insight, stored.Category); // untouched delta survives
    Assert.Equal("keep me", stored.UsageHint); // untouched delta survives
    Assert.Equal("orig-session", stored.ProvenanceSession); // provenance never rewritten
    Assert.Equal(FixedNow, stored.CreatedAt); // creation facts immutable
    Assert.Equal(FixedNow.AddMinutes(5), stored.UpdatedAt); // clock applied
    Assert.Equal(0, h.BumpCount); // only adds drive the nudge counter
  }

  [Theory]
  [InlineData(/*lang=json,strict*/ """{"id":"3f2a9f0e-1111-2222-3333-444455556666","content":"x"}""")]
  [InlineData(/*lang=json,strict*/ """{"id":"3f2a9f0e-1111-2222-3333-444455556666","expected_version":0,"content":"x"}""")]
  [InlineData(/*lang=json,strict*/ """{"id":"3f2a9f0e-1111-2222-3333-444455556666","expected_version":-2,"content":"x"}""")]
  [InlineData(/*lang=json,strict*/ """{"id":"3f2a9f0e-1111-2222-3333-444455556666","expected_version":"2","content":"x"}""")]
  public async Task Update_ExpectedVersion_MustBePresentIntegerAtLeastOne(string json)
  {
    CapabilityInvocationResult result = await new Harness().Provider().InvokeAsync("update", json, ct: TestContext.Current.CancellationToken);

    Assert.StartsWith("Error [MissingVersion]:", result.Content, StringComparison.Ordinal);
  }

  [Fact]
  public async Task Update_UnparsableId_FailsInvalidId()
  {
    CapabilityInvocationResult result = await new Harness().Provider().InvokeAsync("update",
                             /*lang=json,strict*/
                             """{"id":"not-a-guid","expected_version":1,"content":"x"}""", ct: TestContext.Current.CancellationToken);

    Assert.StartsWith("Error [InvalidId]:", result.Content, StringComparison.Ordinal);
    Assert.Contains("not-a-guid", result.Content, StringComparison.Ordinal);
  }

  [Fact]
  public async Task Update_NothingToUpdate_FailsBeforeFetch()
  {
    Harness h = new();

    CapabilityInvocationResult result = await h.Provider().InvokeAsync("update",
        $$"""{"id":"{{KnownId}}","expected_version":2}""", ct: TestContext.Current.CancellationToken);

    Assert.StartsWith("Error [NothingToUpdate]:", result.Content, StringComparison.Ordinal);
    Assert.Equal(0, h.Store._getCallCount);
  }

  [Fact]
  public async Task Update_StaleExpectedVersion_SurfacesVersionConflictNamingCurrent()
  {
    Harness h = new();
    h.Store._rows[KnownId] = Row(id: KnownId, version: 3, content: "current truth");

    CapabilityInvocationResult result = await h.Provider().InvokeAsync("update",
        $$"""{"id":"{{KnownId}}","expected_version":2,"content":"stale write"}""", ct: TestContext.Current.CancellationToken);

    Assert.True(result.IsError);
    Assert.Equal("Error [VersionConflict]: current stored version is 3.", result.Content);
    Assert.Equal(3, h.Store._rows[KnownId].Version); // conflicting write changed nothing
    Assert.Equal("current truth", h.Store._rows[KnownId].Content);
  }

  [Fact]
  public async Task Update_UnknownId_FailsMemoryNotFound()
  {
    CapabilityInvocationResult result = await new Harness().Provider().InvokeAsync("update",
        $$"""{"id":"{{KnownId}}","expected_version":1,"content":"x"}""", ct: TestContext.Current.CancellationToken);

    Assert.StartsWith("Error [MemoryNotFound]:", result.Content, StringComparison.Ordinal);
  }

  // ---- remove ----

  [Fact]
  public async Task Remove_HappyPath_RemovesRow_ExactOutput()
  {
    Harness h = new();
    h.Store._rows[KnownId] = Row(id: KnownId);

    CapabilityInvocationResult result = await h.Provider().InvokeAsync("remove",
        $$"""{"id":"{{KnownId}}","confirm":true}""", ct: TestContext.Current.CancellationToken);

    Assert.False(result.IsError);
    Assert.Equal($"[memories] removed {KnownFullId}", result.Content);
    Assert.Equal([KnownId], h.Store._deletes);
    Assert.Equal(0, h.BumpCount);
  }

  [Theory]
  [InlineData(/*lang=json,strict*/ """{"id":"3f2a9f0e-1111-2222-3333-444455556666"}""")]
  [InlineData(/*lang=json,strict*/ """{"id":"3f2a9f0e-1111-2222-3333-444455556666","confirm":false}""")]
  [InlineData(/*lang=json,strict*/ """{"id":"3f2a9f0e-1111-2222-3333-444455556666","confirm":"true"}""")]
  [InlineData(/*lang=json,strict*/ """{"id":"3f2a9f0e-1111-2222-3333-444455556666","confirm":1}""")]
  public async Task Remove_ConfirmGate_RequiresExactlyBooleanTrue(string json)
  {
    Harness h = new();

    CapabilityInvocationResult result = await h.Provider().InvokeAsync("remove", json, ct: TestContext.Current.CancellationToken);

    Assert.StartsWith("Error [RemoveNotConfirmed]:", result.Content, StringComparison.Ordinal);
    Assert.Empty(h.Store._deletes);
  }

  [Fact]
  public async Task Remove_UnknownId_FailsMemoryNotFound()
  {
    CapabilityInvocationResult result = await new Harness().Provider().InvokeAsync("remove",
        $$"""{"id":"{{KnownId}}","confirm":true}""", ct: TestContext.Current.CancellationToken);

    Assert.StartsWith("Error [MemoryNotFound]:", result.Content, StringComparison.Ordinal);
  }

  // ---- cross-cutting strictness ----

  [Theory]
  [InlineData("search", /*lang=json,strict*/ """{"bogus":1}""")]
  [InlineData("add", /*lang=json,strict*/ """{"bogus":1}""")]
  [InlineData("update", /*lang=json,strict*/ """{"bogus":1}""")]
  [InlineData("remove", /*lang=json,strict*/ """{"bogus":1}""")]
  public async Task UnknownParameter_Rejected_OnEveryAction(string action, string json)
  {
    CapabilityInvocationResult result = await new Harness().Provider().InvokeAsync(action, json, ct: TestContext.Current.CancellationToken);

    Assert.True(result.IsError);
    Assert.StartsWith("Error [InvalidActionInput]:", result.Content, StringComparison.Ordinal);
    Assert.Contains("Unknown parameter 'bogus'", result.Content, StringComparison.Ordinal);
  }

  [Fact]
  public async Task MalformedJson_TypedInputError()
  {
    CapabilityInvocationResult result = await new Harness().Provider().InvokeAsync("search", "{oops", ct: TestContext.Current.CancellationToken);

    Assert.True(result.IsError);
    Assert.StartsWith("Error [InvalidActionInput]:", result.Content, StringComparison.Ordinal);
    Assert.Contains("not valid JSON", result.Content, StringComparison.Ordinal);
  }

  [Fact]
  public async Task NonObjectJson_Rejected()
  {
    CapabilityInvocationResult result = await new Harness().Provider().InvokeAsync("search", "[1,2]", ct: TestContext.Current.CancellationToken);

    Assert.True(result.IsError);
    Assert.StartsWith("Error [InvalidActionInput]:", result.Content, StringComparison.Ordinal);
    Assert.Contains("JSON object", result.Content, StringComparison.Ordinal);
  }

  [Fact]
  public async Task UnknownAction_ReturnsTypedError()
  {
    CapabilityInvocationResult result = await new Harness().Provider().InvokeAsync("upsert", "{}", ct: TestContext.Current.CancellationToken);

    Assert.True(result.IsError);
    Assert.Equal("Error [UnknownAction]: Unknown action: upsert.", result.Content);
  }

  private sealed class FakeCuratedMemoryStore : ICuratedMemoryStore
  {
    internal Dictionary<Guid, CuratedMemory> _rows = [];
    internal Result<CuratedMemory>? _addResultOverride;

    internal int _addCallCount;
    internal int _getCallCount;
    internal string? _lastSearchWorkspaceId;
    internal string? _lastSearchQuery;
    internal MemoryCategory? _lastSearchCategory;
    internal IReadOnlyList<string>? _lastSearchTags;
    internal int _lastSearchLimit;
    internal List<Guid> _deletes = [];

    public Task<Result<CuratedMemory>> AddAsync(CuratedMemory memory, CancellationToken ct = default)
    {
      _addCallCount++;
      if (_addResultOverride is { } overridden)
      {
        return Task.FromResult(overridden);
      }

      _rows[memory.Id] = memory;
      return Task.FromResult(Result.Success(memory));
    }

    public Task<Result<CuratedMemory?>> GetAsync(Guid id, CancellationToken ct = default)
    {
      _getCallCount++;
      return Task.FromResult(Result.Success(_rows.GetValueOrDefault(id)));
    }

    public Task<Result<IReadOnlyList<CuratedMemory>>> SearchAsync(
        string? workspaceId, string? query, MemoryCategory? category,
        IReadOnlyList<string>? tags, int limit, CancellationToken ct = default)
    {
      _lastSearchWorkspaceId = workspaceId;
      _lastSearchQuery = query;
      _lastSearchCategory = category;
      _lastSearchTags = tags;
      _lastSearchLimit = limit;

      IEnumerable<CuratedMemory> visible = _rows.Values
          .Where(m => m.Scope == MemoryScope.Global || m.WorkspaceId == workspaceId)
          .OrderByDescending(m => m.UpdatedAt);
      if (!string.IsNullOrWhiteSpace(query))
      {
        visible = visible.Where(m => m.Content.Contains(query, StringComparison.OrdinalIgnoreCase));
      }

      if (category is not null)
      {
        visible = visible.Where(m => m.Category == category);
      }

      if (tags is { Count: > 0 })
      {
        visible = visible.Where(m => tags.All(t => m.Tags.Contains(t)));
      }

      return Task.FromResult(Result.Success<IReadOnlyList<CuratedMemory>>([.. visible.Take(limit)]));
    }

    public Task<Result<CuratedMemory>> UpdateAsync(CuratedMemory updated, CancellationToken ct = default)
    {
      if (!_rows.TryGetValue(updated.Id, out CuratedMemory? stored))
      {
        return Task.FromResult(Result.Failure<CuratedMemory>(new DomainError(
            CuratedMemoryErrors.MemoryNotFound,
            $"No curated memory with id '{updated.Id}'.")));
      }

      if (stored.Version != updated.Version - 1)
      {
        return Task.FromResult(Result.Failure<CuratedMemory>(new DomainError(
            CuratedMemoryErrors.VersionConflict,
            $"current stored version is {stored.Version}.")));
      }

      _rows[updated.Id] = updated;
      return Task.FromResult(Result.Success(updated));
    }

    public Task<Result<bool>> DeleteAsync(Guid id, CancellationToken ct = default)
    {
      _deletes.Add(id);
      return Task.FromResult(Result.Success(_rows.Remove(id)));
    }
  }
}
