using eThangAgent.SharedKernel;
using eThangAgent.SkillDomain;
using eThangAgent.ToolDomain;

namespace eThangAgent.ToolDomain.Tests;

public class SkillViewToolTests
{
    private static SkillDefinition Def(string name, string body,
        int version = 1, SkillSource source = SkillSource.BuiltIn) =>
        new(name, "description", body, version, source,
            ProvenanceSessionId: null, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch);

    private static (SkillViewTool Tool, FakeLearnedStore Store) MakeTool(
        IReadOnlyList<SkillDefinition>? builtIns = null,
        IReadOnlyList<SkillDefinition>? learned = null,
        bool failAppendUsage = false)
    {
        var store = new FakeLearnedStore(learned ?? [], failAppendUsage);
        return (new SkillViewTool(new FakeCatalog(builtIns ?? []), store), store);
    }

    // ---- Input strictness ----

    [Fact]
    public async Task MissingName_MissingParameter()
    {
        var (tool, _) = MakeTool();
        var result = await tool.ExecuteAsync(new RawToolInput("skill_view", "{}"));
        Assert.True(result.IsError);
        Assert.Contains("MissingParameter", result.Content);
        Assert.Contains("'name'", result.Content);
    }

    [Fact]
    public async Task UnknownParameter_Rejected()
    {
        var (tool, _) = MakeTool();
        var result = await tool.ExecuteAsync(new RawToolInput("skill_view",
            """{"name":"a","encoding":"utf16"}"""));
        Assert.True(result.IsError);
        Assert.Contains("Unknown parameter", result.Content);
        Assert.Contains("encoding", result.Content);
    }

    [Fact]
    public async Task Name_MustBeString_NumberRejected()
    {
        var (tool, _) = MakeTool();
        var result = await tool.ExecuteAsync(new RawToolInput("skill_view",
            """{"name":42}"""));
        Assert.True(result.IsError);
        Assert.Contains("InvalidParameterType", result.Content);
        Assert.Contains("string", result.Content);
    }

    [Fact]
    public async Task Name_MustBeNonEmpty()
    {
        var (tool, _) = MakeTool();
        var result = await tool.ExecuteAsync(new RawToolInput("skill_view",
            """{"name":""}"""));
        Assert.True(result.IsError);
        Assert.Contains("non-empty", result.Content);
    }

    [Fact]
    public async Task InvalidJsonArguments_Rejected()
    {
        var (tool, _) = MakeTool();
        var result = await tool.ExecuteAsync(new RawToolInput("skill_view", "{bad"));
        Assert.True(result.IsError);
        Assert.Contains("not valid JSON", result.Content);
    }

    [Fact]
    public async Task NonObjectArguments_Rejected()
    {
        var (tool, _) = MakeTool();
        var result = await tool.ExecuteAsync(new RawToolInput("skill_view", "[]"));
        Assert.True(result.IsError);
        Assert.Contains("JSON object", result.Content);
    }

    // ---- Resolution & output format ----

    [Fact]
    public async Task BuiltInHit_AnnotationExact_BodyVerbatim_RecordsUsage()
    {
        var (tool, store) = MakeTool(builtIns: [Def("brainstorming", "First line.\nSecond line.")]);

        var result = await tool.ExecuteAsync(new RawToolInput("skill_view",
            """{"name":"brainstorming"}"""));

        Assert.False(result.IsError);
        Assert.Equal("[skill brainstorming | builtin | v1]\nFirst line.\nSecond line.",
            result.Content);

        var call = Assert.Single(store.UsageCalls);
        Assert.Equal("brainstorming", call.Name);
        Assert.True(call.ViewedAt >= DateTimeOffset.UtcNow.AddMinutes(-1),
            "usage timestamp should be roughly now");
    }

    [Fact]
    public async Task LearnedHit_WhenCatalogMisses_RecordsUsage()
    {
        var (tool, store) = MakeTool(learned: [Def("my-skill", "Learned body.", version: 3,
            source: SkillSource.Learned)]);

        var result = await tool.ExecuteAsync(new RawToolInput("skill_view",
            """{"name":"my-skill"}"""));

        Assert.False(result.IsError);
        Assert.Equal("[skill my-skill | learned | v3]\nLearned body.", result.Content);
        Assert.Single(store.UsageCalls);
    }

    [Fact]
    public async Task BuiltInWins_OverSameNamedLearned()
    {
        var (tool, _) = MakeTool(
            builtIns: [Def("shared", "Built-in body.", version: 1)],
            learned: [Def("shared", "Learned body.", version: 7, source: SkillSource.Learned)]);

        var result = await tool.ExecuteAsync(new RawToolInput("skill_view",
            """{"name":"shared"}"""));

        Assert.False(result.IsError);
        Assert.Equal("[skill shared | builtin | v1]\nBuilt-in body.", result.Content);
    }

    [Fact]
    public async Task UnknownName_SkillNotFound()
    {
        var (tool, _) = MakeTool();
        var result = await tool.ExecuteAsync(new RawToolInput("skill_view",
            """{"name":"nope"}"""));
        Assert.True(result.IsError);
        Assert.Contains("Error [SkillNotFound]", result.Content);
        Assert.Contains("nope", result.Content);
    }

    // ---- Best-effort usage recording ----

    [Fact]
    public async Task UsageRecordingFailure_WarningAppended_ViewStillSucceeds()
    {
        var (tool, _) = MakeTool(builtIns: [Def("brainstorming", "The body.")],
            failAppendUsage: true);

        var result = await tool.ExecuteAsync(new RawToolInput("skill_view",
            """{"name":"brainstorming"}"""));

        Assert.False(result.IsError);
        Assert.Equal("[skill brainstorming | builtin | v1]\nThe body." +
                     "\n[warning] usage not recorded", result.Content);
    }

    private sealed class FakeCatalog(IReadOnlyList<SkillDefinition> skills) : ISkillCatalog
    {
        public Task<Result<IReadOnlyList<SkillDefinition>>> ListAsync(CancellationToken ct = default) =>
            Task.FromResult(Result<IReadOnlyList<SkillDefinition>>.Success(skills));

        public Task<Result<SkillDefinition>> GetAsync(string name, CancellationToken ct = default)
        {
            var match = skills.FirstOrDefault(s => s.Name == name);
            return Task.FromResult(match is not null
                ? Result<SkillDefinition>.Success(match)
                : Result<SkillDefinition>.Failure(new Error("SkillNotFound",
                    $"No built-in skill named '{name}'.")));
        }
    }

    private sealed class FakeLearnedStore(IReadOnlyList<SkillDefinition> skills, bool failAppendUsage)
        : ILearnedSkillStore
    {
        public sealed record UsageCall(string Name, DateTimeOffset ViewedAt);

        public List<UsageCall> UsageCalls { get; } = [];

        public Task<Result<IReadOnlyList<SkillDefinition>>> ListAsync(CancellationToken ct = default) =>
            Task.FromResult(Result<IReadOnlyList<SkillDefinition>>.Success(skills));

        public Task<Result<SkillDefinition?>> GetAsync(string name, CancellationToken ct = default) =>
            Task.FromResult(Result<SkillDefinition?>.Success(
                skills.FirstOrDefault(s => s.Name == name)));

        public Task<Result<int>> AppendUsageAsync(string name, DateTimeOffset viewedAt, CancellationToken ct = default)
        {
            UsageCalls.Add(new UsageCall(name, viewedAt));
            return Task.FromResult(failAppendUsage
                ? Result<int>.Failure(new Error("StoreUnavailable", "store down"))
                : Result<int>.Success(1));
        }

        public Task<Result<SkillDefinition>> CreateAsync(SkillDefinition skill, CancellationToken ct = default) =>
            throw new NotSupportedException("Not exercised by these tests.");

        public Task<Result<SkillDefinition>> UpdateAsync(SkillDefinition updated, CancellationToken ct = default) =>
            throw new NotSupportedException("Not exercised by these tests.");

        public Task<Result<bool>> DeleteAsync(string name, CancellationToken ct = default) =>
            throw new NotSupportedException("Not exercised by these tests.");
    }
}
