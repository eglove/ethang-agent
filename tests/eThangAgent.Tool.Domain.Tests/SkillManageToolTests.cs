using eThangAgent.SharedKernel;
using eThangAgent.SkillDomain;

namespace eThangAgent.ToolDomain.Tests;

public class SkillManageToolTests
{
  private static readonly DateTimeOffset ClockNow = new(2026, 8, 21, 12, 0, 0, TimeSpan.Zero);

  private static SkillDefinition Def(string name, string body,
      int version = 1, SkillSource source = SkillSource.BuiltIn,
      string? provenance = null, DateTimeOffset? createdAt = null) =>
      new(name, "description", body, version, source, provenance,
          createdAt ?? DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch);

  private static (SkillManageTool Tool, FakeCatalog Catalog, FakeLearnedStore Store) MakeTool(
      IReadOnlyList<SkillDefinition>? builtIns = null,
      IReadOnlyList<SkillDefinition>? learned = null)
  {
    FakeCatalog catalog = new(builtIns ?? []);
    FakeLearnedStore store = new(learned ?? []);
    return (new SkillManageTool(catalog, store, () => ClockNow), catalog, store);
  }

  // ---- Group 1: action strictness ----

  [Fact]
  public async Task MissingAction_MissingParameter_NamingAllowedActions()
  {
    (SkillManageTool? tool, FakeCatalog _, FakeLearnedStore _) = MakeTool();
    ToolResult result = await tool.ExecuteAsync(new RawToolInput("skill_manage", /*lang=json,strict*/ "{\"timeoutSeconds\":120}"));
    Assert.True(result.IsError);
    Assert.Contains("MissingParameter", result.Content, StringComparison.Ordinal);
    Assert.Contains("'action'", result.Content, StringComparison.Ordinal);
    Assert.Contains("Create", result.Content, StringComparison.Ordinal);
    Assert.Contains("Update", result.Content, StringComparison.Ordinal);
    Assert.Contains("Delete", result.Content, StringComparison.Ordinal);
  }

  [Fact]
  public async Task UnknownActionString_InvalidParameterValue_NamingAllowedActions()
  {
    (SkillManageTool? tool, FakeCatalog _, FakeLearnedStore? store) = MakeTool();
    ToolResult result = await tool.ExecuteAsync(new RawToolInput("skill_manage",
                                 /*lang=json,strict*/
                                 """{"timeoutSeconds":120,"action":"create","name":"x-skill"}"""));
    Assert.True(result.IsError);
    Assert.Contains("InvalidParameterValue", result.Content, StringComparison.Ordinal);
    Assert.Contains("'create'", result.Content, StringComparison.Ordinal);
    Assert.Contains("case-sensitive", result.Content, StringComparison.Ordinal);
    Assert.Contains("Create", result.Content, StringComparison.Ordinal);
    Assert.Contains("Update", result.Content, StringComparison.Ordinal);
    Assert.Contains("Delete", result.Content, StringComparison.Ordinal);
    Assert.Empty(store.GetCalls);
  }

  [Fact]
  public async Task ActionMustBeString_NumberRejected()
  {
    (SkillManageTool? tool, FakeCatalog _, FakeLearnedStore _) = MakeTool();
    ToolResult result = await tool.ExecuteAsync(new RawToolInput("skill_manage",
                                 /*lang=json,strict*/
                                 """{"timeoutSeconds":120,"action":42}"""));
    Assert.True(result.IsError);
    Assert.Contains("InvalidParameterType", result.Content, StringComparison.Ordinal);
    Assert.Contains("'action'", result.Content, StringComparison.Ordinal);
  }

  // ---- Group 2: name charset violations ----

  [Fact]
  public async Task Name_Uppercase_InvalidParameterValue_QuotingRule()
  {
    (SkillManageTool? tool, FakeCatalog _, FakeLearnedStore _) = MakeTool();
    ToolResult result = await tool.ExecuteAsync(new RawToolInput("skill_manage",
                                 /*lang=json,strict*/
                                 """{"timeoutSeconds":120,"action":"Create","name":"My-Skill"}"""));
    Assert.True(result.IsError);
    Assert.Contains("InvalidParameterValue", result.Content, StringComparison.Ordinal);
    Assert.Contains("lowercase", result.Content, StringComparison.Ordinal);
  }

  [Fact]
  public async Task Name_LeadingHyphen_InvalidParameterValue_QuotingRule()
  {
    (SkillManageTool? tool, FakeCatalog _, FakeLearnedStore _) = MakeTool();
    ToolResult result = await tool.ExecuteAsync(new RawToolInput("skill_manage",
                                 /*lang=json,strict*/
                                 """{"timeoutSeconds":120,"action":"Create","name":"-bad"}"""));
    Assert.True(result.IsError);
    Assert.Contains("InvalidParameterValue", result.Content, StringComparison.Ordinal);
    Assert.Contains("must start with a letter or digit", result.Content, StringComparison.Ordinal);
  }

  [Fact]
  public async Task Name_Empty_InvalidParameterValue()
  {
    (SkillManageTool? tool, FakeCatalog _, FakeLearnedStore _) = MakeTool();
    ToolResult result = await tool.ExecuteAsync(new RawToolInput("skill_manage",
                                 /*lang=json,strict*/
                                 """{"timeoutSeconds":120,"action":"Create","name":""}"""));
    Assert.True(result.IsError);
    Assert.Contains("InvalidParameterValue", result.Content, StringComparison.Ordinal);
    Assert.Contains("non-empty", result.Content, StringComparison.Ordinal);
  }

  // ---- Input robustness (house suite conventions) ----

  [Fact]
  public async Task InvalidJsonArguments_Rejected()
  {
    (SkillManageTool? tool, FakeCatalog _, FakeLearnedStore _) = MakeTool();
    ToolResult result = await tool.ExecuteAsync(new RawToolInput("skill_manage", "{\"timeoutSeconds\":120,bad"));
    Assert.True(result.IsError);
    Assert.Contains("not valid JSON", result.Content, StringComparison.Ordinal);
  }

  [Fact]
  public async Task NonObjectArguments_Rejected()
  {
    (SkillManageTool? tool, FakeCatalog _, FakeLearnedStore _) = MakeTool();
    ToolResult result = await tool.ExecuteAsync(new RawToolInput("skill_manage", "[]"));
    Assert.True(result.IsError);
    Assert.Contains("JSON object", result.Content, StringComparison.Ordinal);
  }

  // ---- Group 11: unknown parameter ----

  [Fact]
  public async Task UnknownParameter_Rejected()
  {
    (SkillManageTool? tool, FakeCatalog _, FakeLearnedStore _) = MakeTool();
    ToolResult result = await tool.ExecuteAsync(new RawToolInput("skill_manage",
                                 /*lang=json,strict*/
                                 """{"timeoutSeconds":120,"action":"Delete","name":"my-skill","confirm":true,"force":true}"""));
    Assert.True(result.IsError);
    Assert.Contains("Unknown parameter", result.Content, StringComparison.Ordinal);
    Assert.Contains("force", result.Content, StringComparison.Ordinal);
  }

  // ---- Group 3: create happy path ----

  [Fact]
  public async Task Create_HappyPath_StoreReceivesV1LearnedWithProvenance_ExactOutput()
  {
    (SkillManageTool? tool, FakeCatalog? catalog, FakeLearnedStore? store) = MakeTool();

    ToolResult result = await tool.ExecuteAsync(new RawToolInput("skill_manage",
                                 /*lang=json,strict*/
                                 """{"timeoutSeconds":120,"action":"Create","name":"my-skill","description":"What it does.","body":"Step one.","provenanceSession":"sess-1"}"""));

    Assert.False(result.IsError);
    Assert.Equal("[skill-manage] created 'my-skill' v1", result.Content);

    Assert.Equal(1, catalog.GetCalls);
    string call = Assert.Single(store.GetCalls);
    Assert.Equal("my-skill", call);

    SkillDefinition created = Assert.Single(store.CreateCalls);
    Assert.Equal("my-skill", created.Name);
    Assert.Equal("What it does.", created.Description);
    Assert.Equal("Step one.", created.Body);
    Assert.Equal(1, created.Version);
    Assert.Equal(SkillSource.Learned, created.Source);
    Assert.Equal("sess-1", created.ProvenanceSessionId);
    Assert.Equal(ClockNow, created.CreatedAt);
    Assert.Equal(ClockNow, created.UpdatedAt);
  }

  // ---- Group 4: create over built-in name ----

  [Fact]
  public async Task Create_OverBuiltIn_NameCollision_StoreNeverCalled()
  {
    (SkillManageTool? tool, FakeCatalog? catalog, FakeLearnedStore? store) =
        MakeTool(builtIns: [Def("brainstorming", "Built-in body.")]);

    ToolResult result = await tool.ExecuteAsync(new RawToolInput("skill_manage",
                                 /*lang=json,strict*/
                                 """{"timeoutSeconds":120,"action":"Create","name":"brainstorming","description":"d","body":"b"}"""));

    Assert.True(result.IsError);
    Assert.Contains("NameCollision", result.Content, StringComparison.Ordinal);
    Assert.Contains("authoritative", result.Content, StringComparison.Ordinal);
    Assert.Equal(1, catalog.GetCalls);
    Assert.Empty(store.GetCalls);
    Assert.Empty(store.CreateCalls);
  }

  // ---- Group 5: create over existing learned skill ----

  [Fact]
  public async Task Create_OverExistingLearned_SkillExists()
  {
    (SkillManageTool _, FakeCatalog _, FakeLearnedStore? store) = MakeTool(
        learned: [Def("my-skill", "Existing.", source: SkillSource.Learned)]);
    SkillManageTool tool = new(new FakeCatalog([]), store, () => ClockNow);

    ToolResult result = await tool.ExecuteAsync(new RawToolInput("skill_manage",
                                 /*lang=json,strict*/
                                 """{"timeoutSeconds":120,"action":"Create","name":"my-skill","description":"d","body":"b"}"""));

    Assert.True(result.IsError);
    Assert.Contains("SkillExists", result.Content, StringComparison.Ordinal);
    Assert.Contains("my-skill", result.Content, StringComparison.Ordinal);
    Assert.Empty(store.CreateCalls);
  }

  // ---- Group 6: create missing description / body / empty values ----

  [Fact]
  public async Task Create_MissingDescription_MissingParameter()
  {
    (SkillManageTool? tool, FakeCatalog _, FakeLearnedStore _) = MakeTool();
    ToolResult result = await tool.ExecuteAsync(new RawToolInput("skill_manage",
                                 /*lang=json,strict*/
                                 """{"timeoutSeconds":120,"action":"Create","name":"my-skill","body":"b"}"""));
    Assert.True(result.IsError);
    Assert.Contains("MissingParameter", result.Content, StringComparison.Ordinal);
    Assert.Contains("'description'", result.Content, StringComparison.Ordinal);
  }

  [Fact]
  public async Task Create_MissingBody_MissingParameter()
  {
    (SkillManageTool? tool, FakeCatalog _, FakeLearnedStore _) = MakeTool();
    ToolResult result = await tool.ExecuteAsync(new RawToolInput("skill_manage",
                                 /*lang=json,strict*/
                                 """{"timeoutSeconds":120,"action":"Create","name":"my-skill","description":"d"}"""));
    Assert.True(result.IsError);
    Assert.Contains("MissingParameter", result.Content, StringComparison.Ordinal);
    Assert.Contains("'body'", result.Content, StringComparison.Ordinal);
  }

  [Fact]
  public async Task Create_EmptyDescription_InvalidParameterValue()
  {
    (SkillManageTool? tool, FakeCatalog _, FakeLearnedStore? store) = MakeTool();
    ToolResult result = await tool.ExecuteAsync(new RawToolInput("skill_manage",
                                 /*lang=json,strict*/
                                 """{"timeoutSeconds":120,"action":"Create","name":"my-skill","description":"","body":"b"}"""));
    Assert.True(result.IsError);
    Assert.Contains("InvalidParameterValue", result.Content, StringComparison.Ordinal);
    Assert.Contains("'description'", result.Content, StringComparison.Ordinal);
    Assert.Empty(store.CreateCalls);
  }

  // ---- Group 7: update happy path ----

  [Fact]
  public async Task Update_HappyPath_BumpsVersion_PreservesCreation_ClockUpdatedAt_ExactOutput()
  {
    DateTimeOffset createdAt = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    (SkillManageTool? tool, FakeCatalog _, FakeLearnedStore? store) = MakeTool(
        learned: [Def("my-skill", "Old body.", version: 1,
                source: SkillSource.Learned, provenance: "p0", createdAt: createdAt)]);

    ToolResult result = await tool.ExecuteAsync(new RawToolInput("skill_manage",
                                 /*lang=json,strict*/
                                 """{"timeoutSeconds":120,"action":"Update","name":"my-skill","body":"New body."}"""));

    Assert.False(result.IsError);
    Assert.Equal("[skill-manage] updated 'my-skill' v2", result.Content);

    SkillDefinition updated = Assert.Single(store.UpdateCalls);
    Assert.Equal("my-skill", updated.Name);
    Assert.Equal(2, updated.Version);
    Assert.Equal(createdAt, updated.CreatedAt);
    Assert.Equal("p0", updated.ProvenanceSessionId);
    Assert.Equal("New body.", updated.Body);
    Assert.Equal("description", updated.Description);
    Assert.Equal(ClockNow, updated.UpdatedAt);
  }

  // ---- Group 8: update built-in ----

  [Fact]
  public async Task Update_BuiltIn_BuiltInImmutable_StoreNeverCalled()
  {
    (SkillManageTool? tool, FakeCatalog? catalog, FakeLearnedStore? store) =
        MakeTool(builtIns: [Def("brainstorming", "Built-in body.")]);

    ToolResult result = await tool.ExecuteAsync(new RawToolInput("skill_manage",
                                 /*lang=json,strict*/
                                 """{"timeoutSeconds":120,"action":"Update","name":"brainstorming","body":"New body."}"""));

    Assert.True(result.IsError);
    Assert.Contains("BuiltInImmutable", result.Content, StringComparison.Ordinal);
    Assert.Equal(1, catalog.GetCalls);
    Assert.Empty(store.GetCalls);
    Assert.Empty(store.UpdateCalls);
  }

  // ---- Group 9: update unknown learned / no fields ----

  [Fact]
  public async Task Update_UnknownLearned_SkillNotFound()
  {
    (SkillManageTool? tool, FakeCatalog _, FakeLearnedStore? store) = MakeTool();

    ToolResult result = await tool.ExecuteAsync(new RawToolInput("skill_manage",
                                 /*lang=json,strict*/
                                 """{"timeoutSeconds":120,"action":"Update","name":"nope","body":"New body."}"""));

    Assert.True(result.IsError);
    Assert.Contains("SkillNotFound", result.Content, StringComparison.Ordinal);
    Assert.Contains("nope", result.Content, StringComparison.Ordinal);
    Assert.Empty(store.UpdateCalls);
  }

  [Fact]
  public async Task Update_NoDescriptionNorBody_InvalidParameterValue_StoreNeverReached()
  {
    (SkillManageTool? tool, FakeCatalog _, FakeLearnedStore? store) = MakeTool(
        learned: [Def("my-skill", "Old body.", source: SkillSource.Learned)]);

    ToolResult result = await tool.ExecuteAsync(new RawToolInput("skill_manage",
                                 /*lang=json,strict*/
                                 """{"timeoutSeconds":120,"action":"Update","name":"my-skill"}"""));

    Assert.True(result.IsError);
    Assert.Contains("InvalidParameterValue", result.Content, StringComparison.Ordinal);
    Assert.Contains("description", result.Content, StringComparison.Ordinal);
    Assert.Contains("body", result.Content, StringComparison.Ordinal);
    Assert.Empty(store.GetCalls);
    Assert.Empty(store.UpdateCalls);
  }

  [Fact]
  public async Task Update_EmptyBody_InvalidParameterValue()
  {
    (SkillManageTool? tool, FakeCatalog _, FakeLearnedStore? store) = MakeTool(
        learned: [Def("my-skill", "Old body.", source: SkillSource.Learned)]);

    ToolResult result = await tool.ExecuteAsync(new RawToolInput("skill_manage",
                                 /*lang=json,strict*/
                                 """{"timeoutSeconds":120,"action":"Update","name":"my-skill","body":""}"""));

    Assert.True(result.IsError);
    Assert.Contains("InvalidParameterValue", result.Content, StringComparison.Ordinal);
    Assert.Contains("'body'", result.Content, StringComparison.Ordinal);
    Assert.Empty(store.UpdateCalls);
  }

  // ---- Group 10: delete gate, built-in protection, happy path ----

  [Fact]
  public async Task Delete_WithoutConfirm_InvalidParameterValue_GateExplained()
  {
    (SkillManageTool? tool, FakeCatalog _, FakeLearnedStore? store) = MakeTool(
        learned: [Def("my-skill", "Body.", source: SkillSource.Learned)]);

    ToolResult result = await tool.ExecuteAsync(new RawToolInput("skill_manage",
                                 /*lang=json,strict*/
                                 """{"timeoutSeconds":120,"action":"Delete","name":"my-skill"}"""));

    Assert.True(result.IsError);
    Assert.Contains("InvalidParameterValue", result.Content, StringComparison.Ordinal);
    Assert.Contains("'confirm'", result.Content, StringComparison.Ordinal);
    Assert.Contains("true", result.Content, StringComparison.Ordinal);
    Assert.Empty(store.DeleteCalls);
  }

  [Fact]
  public async Task Delete_ConfirmFalse_InvalidParameterValue_GateExplained()
  {
    (SkillManageTool? tool, FakeCatalog _, FakeLearnedStore? store) = MakeTool(
        learned: [Def("my-skill", "Body.", source: SkillSource.Learned)]);

    ToolResult result = await tool.ExecuteAsync(new RawToolInput("skill_manage",
                                 /*lang=json,strict*/
                                 """{"timeoutSeconds":120,"action":"Delete","name":"my-skill","confirm":false}"""));

    Assert.True(result.IsError);
    Assert.Contains("InvalidParameterValue", result.Content, StringComparison.Ordinal);
    Assert.Contains("'confirm'", result.Content, StringComparison.Ordinal);
    Assert.Empty(store.DeleteCalls);
  }

  [Fact]
  public async Task Delete_ConfirmNotBoolean_RejectedByGate()
  {
    (SkillManageTool? tool, FakeCatalog _, FakeLearnedStore? store) = MakeTool(
        learned: [Def("my-skill", "Body.", source: SkillSource.Learned)]);

    ToolResult result = await tool.ExecuteAsync(new RawToolInput("skill_manage",
                                 /*lang=json,strict*/
                                 """{"timeoutSeconds":120,"action":"Delete","name":"my-skill","confirm":"true"}"""));

    Assert.True(result.IsError);
    Assert.Contains("InvalidParameterValue", result.Content, StringComparison.Ordinal);
    Assert.Empty(store.DeleteCalls);
  }

  [Fact]
  public async Task Delete_BuiltIn_BuiltInImmutable_StoreNeverCalled()
  {
    (SkillManageTool? tool, FakeCatalog? catalog, FakeLearnedStore? store) =
        MakeTool(builtIns: [Def("brainstorming", "Built-in body.")]);

    ToolResult result = await tool.ExecuteAsync(new RawToolInput("skill_manage",
                                 /*lang=json,strict*/
                                 """{"timeoutSeconds":120,"action":"Delete","name":"brainstorming","confirm":true}"""));

    Assert.True(result.IsError);
    Assert.Contains("BuiltInImmutable", result.Content, StringComparison.Ordinal);
    Assert.Equal(1, catalog.GetCalls);
    Assert.Empty(store.GetCalls);
    Assert.Empty(store.DeleteCalls);
  }

  [Fact]
  public async Task Delete_HappyPath_StoreDeleteCalled_ExactOutput()
  {
    (SkillManageTool? tool, FakeCatalog _, FakeLearnedStore? store) = MakeTool(
        learned: [Def("my-skill", "Body.", version: 3,
                source: SkillSource.Learned, provenance: "p0")]);

    ToolResult result = await tool.ExecuteAsync(new RawToolInput("skill_manage",
                                 /*lang=json,strict*/
                                 """{"timeoutSeconds":120,"action":"Delete","name":"my-skill","confirm":true}"""));

    Assert.False(result.IsError);
    Assert.Equal("[skill-manage] deleted 'my-skill'", result.Content);

    Assert.Equal("my-skill", Assert.Single(store.GetCalls));
    Assert.Equal("my-skill", Assert.Single(store.DeleteCalls));
  }

  private sealed class FakeCatalog(IReadOnlyList<SkillDefinition> skills) : ISkillCatalog
  {
    public int GetCalls { get; private set; }

    public Task<Result<IReadOnlyList<SkillDefinition>>> ListAsync(CancellationToken ct = default) =>
        Task.FromResult(Result.Success(skills));

    public Task<Result<SkillDefinition>> GetAsync(string name, CancellationToken ct = default)
    {
      GetCalls++;
      SkillDefinition? match = skills.FirstOrDefault(s => s.Name == name);
      return Task.FromResult(match is not null
          ? Result.Success(match)
          : Result.Failure<SkillDefinition>(new DomainError("SkillNotFound",
              $"No built-in skill named '{name}'.")));
    }
  }

  private sealed class FakeLearnedStore(IReadOnlyList<SkillDefinition> skills) : ILearnedSkillStore
  {
    private readonly Dictionary<string, SkillDefinition> _skills = skills.ToDictionary(s => s.Name, StringComparer.Ordinal);

    public List<SkillDefinition> CreateCalls { get; } = [];
    public List<string> GetCalls { get; } = [];
    public List<SkillDefinition> UpdateCalls { get; } = [];
    public List<string> DeleteCalls { get; } = [];

    public Task<Result<SkillDefinition?>> GetAsync(string name, CancellationToken ct = default)
    {
      GetCalls.Add(name);
      return Task.FromResult(Result.Success(
          _skills.TryGetValue(name, out SkillDefinition? skill) ? skill : null));
    }

    public Task<Result<SkillDefinition>> CreateAsync(SkillDefinition skill, CancellationToken ct = default)
    {
      CreateCalls.Add(skill);
      _skills[skill.Name] = skill;
      return Task.FromResult(Result.Success(skill));
    }

    public Task<Result<SkillDefinition>> UpdateAsync(SkillDefinition updated, CancellationToken ct = default)
    {
      UpdateCalls.Add(updated);
      _skills[updated.Name] = updated;
      return Task.FromResult(Result.Success(updated));
    }

    public Task<Result<bool>> DeleteAsync(string name, CancellationToken ct = default)
    {
      DeleteCalls.Add(name);
      return Task.FromResult(Result.Success(_skills.Remove(name)));
    }

    public Task<Result<IReadOnlyList<SkillDefinition>>> ListAsync(CancellationToken ct = default) =>
        Task.FromResult(Result.Success(
            (IReadOnlyList<SkillDefinition>)[.. _skills.Values]));

    public Task<Result<int>> AppendUsageAsync(string name, DateTimeOffset viewedAt, CancellationToken ct = default) =>
        Task.FromResult(Result.Success(1));
  }
}
