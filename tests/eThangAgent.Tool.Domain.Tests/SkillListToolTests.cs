using eThangAgent.SharedKernel;
using eThangAgent.SkillDomain;
using eThangAgent.ToolDomain;

namespace eThangAgent.ToolDomain.Tests;

public class SkillListToolTests
{
    private static SkillDefinition Def(string name, string description,
        int version = 1, SkillSource source = SkillSource.BuiltIn) =>
        new(name, description, "BODY", version, source,
            ProvenanceSessionId: null, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch);

    private static SkillListTool MakeTool(
        IReadOnlyList<SkillDefinition>? builtIns = null,
        IReadOnlyList<SkillDefinition>? learned = null,
        bool failCatalogList = false,
        bool failStoreList = false) =>
        new(new FakeCatalog(builtIns ?? [], failCatalogList),
            new FakeLearnedStore(learned ?? [], failStoreList));

    // ---- Input strictness (zero-parameter tool) ----

    [Fact]
    public async Task EmptyObject_IsAccepted()
    {
        var result = await MakeTool().ExecuteAsync(new RawToolInput("skill_list", "{\"timeoutSeconds\":120}"));
        Assert.False(result.IsError);
    }

    [Theory]
    [InlineData("""{"timeoutSeconds":120,"verbose":true}""")]
    [InlineData("""{"timeoutSeconds":120,"name":"brainstorming"}""")]
    public async Task AnyParameter_IsRejected(string args)
    {
        var result = await MakeTool().ExecuteAsync(new RawToolInput("skill_list", args));
        Assert.True(result.IsError);
        Assert.Contains("Unknown parameter", result.Content);
    }

    [Fact]
    public async Task NonObjectArguments_AreRejected()
    {
        var result = await MakeTool().ExecuteAsync(new RawToolInput("skill_list", "[]"));
        Assert.True(result.IsError);
        Assert.Contains("JSON object", result.Content);
    }

    [Fact]
    public async Task InvalidJsonArguments_AreRejected()
    {
        var result = await MakeTool().ExecuteAsync(new RawToolInput("skill_list", "{\"timeoutSeconds\":120,bad"));
        Assert.True(result.IsError);
        Assert.Contains("not valid JSON", result.Content);
    }

    // ---- Listing format ----

    [Fact]
    public async Task NoSkillsAnywhere_HeaderCountsZero()
    {
        var result = await MakeTool().ExecuteAsync(new RawToolInput("skill_list", "{\"timeoutSeconds\":120}"));
        Assert.False(result.IsError);
        Assert.Equal("[skills: 0 available]", result.Content);
    }

    [Fact]
    public async Task MergesBuiltInAndLearned_SortedByName_TruncatesLongDescriptions()
    {
        var longDescription = new string('d', 61);
        var tool = new SkillListTool(
            new FakeCatalog([Def("brainstorming", longDescription)]),
            new FakeLearnedStore(
                [Def("my-skill", "Remember deployment quirks", version: 3, source: SkillSource.Learned)]));

        var result = await tool.ExecuteAsync(new RawToolInput("skill_list", "{\"timeoutSeconds\":120}"));

        var expected =
            "[skills: 2 available]\n" +
            $"{"brainstorming",-20} builtin v1  {new string('d', 60)}\u2026\n" +
            $"{"my-skill",-20} learned v3  Remember deployment quirks";
        Assert.False(result.IsError);
        Assert.Equal(expected, result.Content);
    }

    [Fact]
    public async Task Description_ExactlySixtyChars_IsNotTruncated()
    {
        var description = new string('d', 60);
        var result = await MakeTool([Def("s", description)])
            .ExecuteAsync(new RawToolInput("skill_list", "{\"timeoutSeconds\":120}"));
        Assert.Equal("[skills: 1 available]\n" + $"{"s",-20} builtin v1  {description}",
            result.Content);
    }

    // ---- Degradation on source failure ----

    [Fact]
    public async Task LearnedStoreFailure_AppendsWarning_StillSucceeds()
    {
        var result = await MakeTool([Def("brainstorming", "short")], failStoreList: true)
            .ExecuteAsync(new RawToolInput("skill_list", "{\"timeoutSeconds\":120}"));
        Assert.False(result.IsError);
        Assert.Equal(
            "[skills: 1 available]\n" +
            $"{"brainstorming",-20} builtin v1  short\n" +
            "[warning] learned skills unavailable: store down",
            result.Content);
    }

    [Fact]
    public async Task CatalogFailure_OmitsBuiltIns_AppendsWarning_StillSucceeds()
    {
        var result = await MakeTool(
            learned: [Def("my-skill", "learned thing", source: SkillSource.Learned)],
            failCatalogList: true)
            .ExecuteAsync(new RawToolInput("skill_list", "{\"timeoutSeconds\":120}"));
        Assert.False(result.IsError);
        Assert.Equal(
            "[skills: 1 available]\n" +
            $"{"my-skill",-20} learned v1  learned thing\n" +
            "[warning] built-in skills unavailable: catalog down",
            result.Content);
    }

    private sealed class FakeCatalog(IReadOnlyList<SkillDefinition> skills, bool failList = false) : ISkillCatalog
    {
        public Task<Result<IReadOnlyList<SkillDefinition>>> ListAsync(CancellationToken ct = default) =>
            Task.FromResult(failList
                ? Result<IReadOnlyList<SkillDefinition>>.Failure(
                    new Error("CatalogUnavailable", "catalog down"))
                : Result<IReadOnlyList<SkillDefinition>>.Success(skills));

        public Task<Result<SkillDefinition>> GetAsync(string name, CancellationToken ct = default)
        {
            var match = skills.FirstOrDefault(s => s.Name == name);
            return Task.FromResult(match is not null
                ? Result<SkillDefinition>.Success(match)
                : Result<SkillDefinition>.Failure(new Error("SkillNotFound",
                    $"No built-in skill named '{name}'.")));
        }
    }

    private sealed class FakeLearnedStore(IReadOnlyList<SkillDefinition> skills, bool failList = false) : ILearnedSkillStore
    {
        public Task<Result<IReadOnlyList<SkillDefinition>>> ListAsync(CancellationToken ct = default) =>
            Task.FromResult(failList
                ? Result<IReadOnlyList<SkillDefinition>>.Failure(
                    new Error("StoreUnavailable", "store down"))
                : Result<IReadOnlyList<SkillDefinition>>.Success(skills));

        public Task<Result<SkillDefinition?>> GetAsync(string name, CancellationToken ct = default) =>
            Task.FromResult(Result<SkillDefinition?>.Success(
                skills.FirstOrDefault(s => s.Name == name)));

        public Task<Result<int>> AppendUsageAsync(string name, DateTimeOffset viewedAt, CancellationToken ct = default) =>
            Task.FromResult(Result<int>.Failure(new Error("StoreUnavailable", "store down")));

        public Task<Result<SkillDefinition>> CreateAsync(SkillDefinition skill, CancellationToken ct = default) =>
            throw new NotSupportedException("Not exercised by these tests.");

        public Task<Result<SkillDefinition>> UpdateAsync(SkillDefinition updated, CancellationToken ct = default) =>
            throw new NotSupportedException("Not exercised by these tests.");

        public Task<Result<bool>> DeleteAsync(string name, CancellationToken ct = default) =>
            throw new NotSupportedException("Not exercised by these tests.");
    }
}
