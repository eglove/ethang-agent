using eThangAgent.SharedKernel;
using eThangAgent.SkillDomain;
using eThangAgent.ToolDomain;

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
        var catalog = new FakeCatalog(builtIns ?? []);
        var store = new FakeLearnedStore(learned ?? []);
        return (new SkillManageTool(catalog, store, () => ClockNow), catalog, store);
    }

    // ---- Group 1: action strictness ----

    [Fact]
    public async Task MissingAction_MissingParameter_NamingAllowedActions()
    {
        var (tool, _, _) = MakeTool();
        var result = await tool.ExecuteAsync(new RawToolInput("skill_manage", "{}"));
        Assert.True(result.IsError);
        Assert.Contains("MissingParameter", result.Content);
        Assert.Contains("'action'", result.Content);
        Assert.Contains("Create", result.Content);
        Assert.Contains("Update", result.Content);
        Assert.Contains("Delete", result.Content);
    }

    [Fact]
    public async Task UnknownActionString_InvalidParameterValue_NamingAllowedActions()
    {
        var (tool, _, store) = MakeTool();
        var result = await tool.ExecuteAsync(new RawToolInput("skill_manage",
            """{"action":"create","name":"x-skill"}"""));
        Assert.True(result.IsError);
        Assert.Contains("InvalidParameterValue", result.Content);
        Assert.Contains("'create'", result.Content);
        Assert.Contains("case-sensitive", result.Content);
        Assert.Contains("Create", result.Content);
        Assert.Contains("Update", result.Content);
        Assert.Contains("Delete", result.Content);
        Assert.Empty(store.GetCalls);
    }

    [Fact]
    public async Task ActionMustBeString_NumberRejected()
    {
        var (tool, _, _) = MakeTool();
        var result = await tool.ExecuteAsync(new RawToolInput("skill_manage",
            """{"action":42}"""));
        Assert.True(result.IsError);
        Assert.Contains("InvalidParameterType", result.Content);
        Assert.Contains("'action'", result.Content);
    }

    // ---- Group 2: name charset violations ----

    [Fact]
    public async Task Name_Uppercase_InvalidParameterValue_QuotingRule()
    {
        var (tool, _, _) = MakeTool();
        var result = await tool.ExecuteAsync(new RawToolInput("skill_manage",
            """{"action":"Create","name":"My-Skill"}"""));
        Assert.True(result.IsError);
        Assert.Contains("InvalidParameterValue", result.Content);
        Assert.Contains("lowercase", result.Content);
    }

    [Fact]
    public async Task Name_LeadingHyphen_InvalidParameterValue_QuotingRule()
    {
        var (tool, _, _) = MakeTool();
        var result = await tool.ExecuteAsync(new RawToolInput("skill_manage",
            """{"action":"Create","name":"-bad"}"""));
        Assert.True(result.IsError);
        Assert.Contains("InvalidParameterValue", result.Content);
        Assert.Contains("must start with a letter or digit", result.Content);
    }

    [Fact]
    public async Task Name_Empty_InvalidParameterValue()
    {
        var (tool, _, _) = MakeTool();
        var result = await tool.ExecuteAsync(new RawToolInput("skill_manage",
            """{"action":"Create","name":""}"""));
        Assert.True(result.IsError);
        Assert.Contains("InvalidParameterValue", result.Content);
        Assert.Contains("non-empty", result.Content);
    }

    // ---- Input robustness (house suite conventions) ----

    [Fact]
    public async Task InvalidJsonArguments_Rejected()
    {
        var (tool, _, _) = MakeTool();
        var result = await tool.ExecuteAsync(new RawToolInput("skill_manage", "{bad"));
        Assert.True(result.IsError);
        Assert.Contains("not valid JSON", result.Content);
    }

    [Fact]
    public async Task NonObjectArguments_Rejected()
    {
        var (tool, _, _) = MakeTool();
        var result = await tool.ExecuteAsync(new RawToolInput("skill_manage", "[]"));
        Assert.True(result.IsError);
        Assert.Contains("JSON object", result.Content);
    }

    // ---- Group 11: unknown parameter ----

    [Fact]
    public async Task UnknownParameter_Rejected()
    {
        var (tool, _, _) = MakeTool();
        var result = await tool.ExecuteAsync(new RawToolInput("skill_manage",
            """{"action":"Delete","name":"my-skill","confirm":true,"force":true}"""));
        Assert.True(result.IsError);
        Assert.Contains("Unknown parameter", result.Content);
        Assert.Contains("force", result.Content);
    }

    // ---- Group 3: create happy path ----

    [Fact]
    public async Task Create_HappyPath_StoreReceivesV1LearnedWithProvenance_ExactOutput()
    {
        var (tool, catalog, store) = MakeTool();

        var result = await tool.ExecuteAsync(new RawToolInput("skill_manage",
            """{"action":"Create","name":"my-skill","description":"What it does.","body":"Step one.","provenanceSession":"sess-1"}"""));

        Assert.False(result.IsError);
        Assert.Equal("[skill-manage] created 'my-skill' v1", result.Content);

        Assert.Equal(1, catalog.GetCalls);
        var call = Assert.Single(store.GetCalls);
        Assert.Equal("my-skill", call);

        var created = Assert.Single(store.CreateCalls);
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
        var (tool, catalog, store) =
            MakeTool(builtIns: [Def("brainstorming", "Built-in body.")]);

        var result = await tool.ExecuteAsync(new RawToolInput("skill_manage",
            """{"action":"Create","name":"brainstorming","description":"d","body":"b"}"""));

        Assert.True(result.IsError);
        Assert.Contains("NameCollision", result.Content);
        Assert.Contains("authoritative", result.Content);
        Assert.Equal(1, catalog.GetCalls);
        Assert.Empty(store.GetCalls);
        Assert.Empty(store.CreateCalls);
    }

    // ---- Group 5: create over existing learned skill ----

    [Fact]
    public async Task Create_OverExistingLearned_SkillExists()
    {
        var (_, _, store) = MakeTool(
            learned: [Def("my-skill", "Existing.", source: SkillSource.Learned)]);
        var tool = new SkillManageTool(new FakeCatalog([]), store, () => ClockNow);

        var result = await tool.ExecuteAsync(new RawToolInput("skill_manage",
            """{"action":"Create","name":"my-skill","description":"d","body":"b"}"""));

        Assert.True(result.IsError);
        Assert.Contains("SkillExists", result.Content);
        Assert.Contains("my-skill", result.Content);
        Assert.Empty(store.CreateCalls);
    }

    // ---- Group 6: create missing description / body / empty values ----

    [Fact]
    public async Task Create_MissingDescription_MissingParameter()
    {
        var (tool, _, _) = MakeTool();
        var result = await tool.ExecuteAsync(new RawToolInput("skill_manage",
            """{"action":"Create","name":"my-skill","body":"b"}"""));
        Assert.True(result.IsError);
        Assert.Contains("MissingParameter", result.Content);
        Assert.Contains("'description'", result.Content);
    }

    [Fact]
    public async Task Create_MissingBody_MissingParameter()
    {
        var (tool, _, _) = MakeTool();
        var result = await tool.ExecuteAsync(new RawToolInput("skill_manage",
            """{"action":"Create","name":"my-skill","description":"d"}"""));
        Assert.True(result.IsError);
        Assert.Contains("MissingParameter", result.Content);
        Assert.Contains("'body'", result.Content);
    }

    [Fact]
    public async Task Create_EmptyDescription_InvalidParameterValue()
    {
        var (tool, _, store) = MakeTool();
        var result = await tool.ExecuteAsync(new RawToolInput("skill_manage",
            """{"action":"Create","name":"my-skill","description":"","body":"b"}"""));
        Assert.True(result.IsError);
        Assert.Contains("InvalidParameterValue", result.Content);
        Assert.Contains("'description'", result.Content);
        Assert.Empty(store.CreateCalls);
    }

    // ---- Group 7: update happy path ----

    [Fact]
    public async Task Update_HappyPath_BumpsVersion_PreservesCreation_ClockUpdatedAt_ExactOutput()
    {
        var createdAt = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var (tool, _, store) = MakeTool(
            learned: [Def("my-skill", "Old body.", version: 1,
                source: SkillSource.Learned, provenance: "p0", createdAt: createdAt)]);

        var result = await tool.ExecuteAsync(new RawToolInput("skill_manage",
            """{"action":"Update","name":"my-skill","body":"New body."}"""));

        Assert.False(result.IsError);
        Assert.Equal("[skill-manage] updated 'my-skill' v2", result.Content);

        var updated = Assert.Single(store.UpdateCalls);
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
        var (tool, catalog, store) =
            MakeTool(builtIns: [Def("brainstorming", "Built-in body.")]);

        var result = await tool.ExecuteAsync(new RawToolInput("skill_manage",
            """{"action":"Update","name":"brainstorming","body":"New body."}"""));

        Assert.True(result.IsError);
        Assert.Contains("BuiltInImmutable", result.Content);
        Assert.Equal(1, catalog.GetCalls);
        Assert.Empty(store.GetCalls);
        Assert.Empty(store.UpdateCalls);
    }

    // ---- Group 9: update unknown learned / no fields ----

    [Fact]
    public async Task Update_UnknownLearned_SkillNotFound()
    {
        var (tool, _, store) = MakeTool();

        var result = await tool.ExecuteAsync(new RawToolInput("skill_manage",
            """{"action":"Update","name":"nope","body":"New body."}"""));

        Assert.True(result.IsError);
        Assert.Contains("SkillNotFound", result.Content);
        Assert.Contains("nope", result.Content);
        Assert.Empty(store.UpdateCalls);
    }

    [Fact]
    public async Task Update_NoDescriptionNorBody_InvalidParameterValue_StoreNeverReached()
    {
        var (tool, _, store) = MakeTool(
            learned: [Def("my-skill", "Old body.", source: SkillSource.Learned)]);

        var result = await tool.ExecuteAsync(new RawToolInput("skill_manage",
            """{"action":"Update","name":"my-skill"}"""));

        Assert.True(result.IsError);
        Assert.Contains("InvalidParameterValue", result.Content);
        Assert.Contains("description", result.Content);
        Assert.Contains("body", result.Content);
        Assert.Empty(store.GetCalls);
        Assert.Empty(store.UpdateCalls);
    }

    [Fact]
    public async Task Update_EmptyBody_InvalidParameterValue()
    {
        var (tool, _, store) = MakeTool(
            learned: [Def("my-skill", "Old body.", source: SkillSource.Learned)]);

        var result = await tool.ExecuteAsync(new RawToolInput("skill_manage",
            """{"action":"Update","name":"my-skill","body":""}"""));

        Assert.True(result.IsError);
        Assert.Contains("InvalidParameterValue", result.Content);
        Assert.Contains("'body'", result.Content);
        Assert.Empty(store.UpdateCalls);
    }

    // ---- Group 10: delete gate, built-in protection, happy path ----

    [Fact]
    public async Task Delete_WithoutConfirm_InvalidParameterValue_GateExplained()
    {
        var (tool, _, store) = MakeTool(
            learned: [Def("my-skill", "Body.", source: SkillSource.Learned)]);

        var result = await tool.ExecuteAsync(new RawToolInput("skill_manage",
            """{"action":"Delete","name":"my-skill"}"""));

        Assert.True(result.IsError);
        Assert.Contains("InvalidParameterValue", result.Content);
        Assert.Contains("'confirm'", result.Content);
        Assert.Contains("true", result.Content);
        Assert.Empty(store.DeleteCalls);
    }

    [Fact]
    public async Task Delete_ConfirmFalse_InvalidParameterValue_GateExplained()
    {
        var (tool, _, store) = MakeTool(
            learned: [Def("my-skill", "Body.", source: SkillSource.Learned)]);

        var result = await tool.ExecuteAsync(new RawToolInput("skill_manage",
            """{"action":"Delete","name":"my-skill","confirm":false}"""));

        Assert.True(result.IsError);
        Assert.Contains("InvalidParameterValue", result.Content);
        Assert.Contains("'confirm'", result.Content);
        Assert.Empty(store.DeleteCalls);
    }

    [Fact]
    public async Task Delete_ConfirmNotBoolean_RejectedByGate()
    {
        var (tool, _, store) = MakeTool(
            learned: [Def("my-skill", "Body.", source: SkillSource.Learned)]);

        var result = await tool.ExecuteAsync(new RawToolInput("skill_manage",
            """{"action":"Delete","name":"my-skill","confirm":"true"}"""));

        Assert.True(result.IsError);
        Assert.Contains("InvalidParameterValue", result.Content);
        Assert.Empty(store.DeleteCalls);
    }

    [Fact]
    public async Task Delete_BuiltIn_BuiltInImmutable_StoreNeverCalled()
    {
        var (tool, catalog, store) =
            MakeTool(builtIns: [Def("brainstorming", "Built-in body.")]);

        var result = await tool.ExecuteAsync(new RawToolInput("skill_manage",
            """{"action":"Delete","name":"brainstorming","confirm":true}"""));

        Assert.True(result.IsError);
        Assert.Contains("BuiltInImmutable", result.Content);
        Assert.Equal(1, catalog.GetCalls);
        Assert.Empty(store.GetCalls);
        Assert.Empty(store.DeleteCalls);
    }

    [Fact]
    public async Task Delete_HappyPath_StoreDeleteCalled_ExactOutput()
    {
        var (tool, _, store) = MakeTool(
            learned: [Def("my-skill", "Body.", version: 3,
                source: SkillSource.Learned, provenance: "p0")]);

        var result = await tool.ExecuteAsync(new RawToolInput("skill_manage",
            """{"action":"Delete","name":"my-skill","confirm":true}"""));

        Assert.False(result.IsError);
        Assert.Equal("[skill-manage] deleted 'my-skill'", result.Content);

        Assert.Equal("my-skill", Assert.Single(store.GetCalls));
        Assert.Equal("my-skill", Assert.Single(store.DeleteCalls));
    }

    private sealed class FakeCatalog(IReadOnlyList<SkillDefinition> skills) : ISkillCatalog
    {
        public int GetCalls { get; private set; }

        public Task<Result<IReadOnlyList<SkillDefinition>>> ListAsync(CancellationToken ct = default) =>
            Task.FromResult(Result<IReadOnlyList<SkillDefinition>>.Success(skills));

        public Task<Result<SkillDefinition>> GetAsync(string name, CancellationToken ct = default)
        {
            GetCalls++;
            var match = skills.FirstOrDefault(s => s.Name == name);
            return Task.FromResult(match is not null
                ? Result<SkillDefinition>.Success(match)
                : Result<SkillDefinition>.Failure(new Error("SkillNotFound",
                    $"No built-in skill named '{name}'.")));
        }
    }

    private sealed class FakeLearnedStore : ILearnedSkillStore
    {
        private readonly Dictionary<string, SkillDefinition> _skills;

        public FakeLearnedStore(IReadOnlyList<SkillDefinition> skills) =>
            _skills = skills.ToDictionary(s => s.Name, StringComparer.Ordinal);

        public List<SkillDefinition> CreateCalls { get; } = [];
        public List<string> GetCalls { get; } = [];
        public List<SkillDefinition> UpdateCalls { get; } = [];
        public List<string> DeleteCalls { get; } = [];

        public Task<Result<SkillDefinition?>> GetAsync(string name, CancellationToken ct = default)
        {
            GetCalls.Add(name);
            return Task.FromResult(Result<SkillDefinition?>.Success(
                _skills.TryGetValue(name, out var skill) ? skill : null));
        }

        public Task<Result<SkillDefinition>> CreateAsync(SkillDefinition skill, CancellationToken ct = default)
        {
            CreateCalls.Add(skill);
            _skills[skill.Name] = skill;
            return Task.FromResult(Result<SkillDefinition>.Success(skill));
        }

        public Task<Result<SkillDefinition>> UpdateAsync(SkillDefinition updated, CancellationToken ct = default)
        {
            UpdateCalls.Add(updated);
            _skills[updated.Name] = updated;
            return Task.FromResult(Result<SkillDefinition>.Success(updated));
        }

        public Task<Result<bool>> DeleteAsync(string name, CancellationToken ct = default)
        {
            DeleteCalls.Add(name);
            return Task.FromResult(Result<bool>.Success(_skills.Remove(name)));
        }

        public Task<Result<IReadOnlyList<SkillDefinition>>> ListAsync(CancellationToken ct = default) =>
            Task.FromResult(Result<IReadOnlyList<SkillDefinition>>.Success(
                (IReadOnlyList<SkillDefinition>)_skills.Values.ToList()));

        public Task<Result<int>> AppendUsageAsync(string name, DateTimeOffset viewedAt, CancellationToken ct = default) =>
            Task.FromResult(Result<int>.Success(1));
    }
}
