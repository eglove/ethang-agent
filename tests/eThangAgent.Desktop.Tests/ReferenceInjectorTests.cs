using eThangAgent.Agent.Application;
using eThangAgent.Desktop.ViewModels;
using eThangAgent.SharedKernel;
using eThangAgent.SkillDomain;
using eThangAgent.ToolDomain;

namespace eThangAgent.Desktop.Tests;

public class ReferenceInjectorTests
{
  private const string Root = "C:/w";

  private static CancellationToken Ct => TestContext.Current.CancellationToken;

  private static readonly FileVersion Version = new(11, new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc));

  private sealed class FakeFiles : IFileSystemAccess
  {
    public List<string> Reads { get; } = [];

    public IReadOnlyList<string> Lines { get; init; } = ["alpha", "beta"];

    public bool Missing { get; init; }

    public Task<Result<FileRead>> ReadLinesAsync(string path, int startLine, int endLine, CancellationToken ct = default)
    {
      Reads.Add(path);
      return Task.FromResult(Missing
          ? Result.Failure<FileRead>(new DomainError("FileNotFound", $"File not found: {path}"))
          : Result.Success(new FileRead(Lines, Lines.Count, Lines.Count, Version)));
    }

    public Task<Result<byte[]>> ReadBytesAsync(string path, CancellationToken ct = default)
        => Task.FromResult(Result.Success(Array.Empty<byte>()));
  }

  private sealed class FakeCatalog(params SkillDefinition[] skills) : ISkillCatalog
  {
    public Task<Result<IReadOnlyList<SkillDefinition>>> ListAsync(CancellationToken ct = default) =>
        Task.FromResult(Result.Success<IReadOnlyList<SkillDefinition>>([.. skills]));

    public Task<Result<SkillDefinition>> GetAsync(string name, CancellationToken ct = default) =>
        Task.FromResult(Result.Failure<SkillDefinition>(new DomainError("SkillNotFound", name)));
  }

  private static SkillDefinition Skill(string name) => new(
      name, "Does " + name + " things", "Body of " + name, 1, SkillSource.BuiltIn);

  private static ReferenceInjector Injector(
      IFileSystemAccess files, SkillInvocationService? skills = null)
    => new(files, skills, Root);

  [Fact]
  public async Task MissingFileRefusesAndNamesThePath()
  {
    ReferenceInjectionResult r = await Injector(new FakeFiles { Missing = true })
        .InjectAsync("look at @nope/Missing.cs", Ct);
    Assert.False(r.IsSuccess);
    Assert.Contains("ReferenceNotFound", r.FailureNotice, StringComparison.Ordinal);
    Assert.Contains("nope/Missing.cs", r.FailureNotice, StringComparison.Ordinal);
  }

  [Fact]
  public async Task FileReferenceAppendsReadToolOutputVerbatim()
  {
    ReferenceInjectionResult r = await Injector(new FakeFiles()).InjectAsync("check @notes.txt", Ct);
    Assert.True(r.IsSuccess);
    Assert.Equal("check @notes.txt", r.MessageText);
    Assert.StartsWith("[read ", r.AppendedBlocks[0], StringComparison.Ordinal);
    Assert.Contains("1→ alpha", r.AppendedBlocks[0], StringComparison.Ordinal);
    Assert.Contains("2→ beta", r.AppendedBlocks[0], StringComparison.Ordinal);
  }

  [Fact]
  public async Task ARepeatedReferenceStillDeliversFullContent()
  {
    // The freshness ledger is the agent's per-turn memory and is reset at turn start, so a
    // reference never elides: the same file named twice delivers its content twice.
    FakeFiles files = new();
    ReferenceInjectionResult r = await Injector(files).InjectAsync("@notes.txt and @notes.txt", Ct);
    Assert.Equal(2, r.AppendedBlocks.Count);
    Assert.All(r.AppendedBlocks, block =>
        Assert.Contains("1→ alpha", block, StringComparison.Ordinal));
  }

  [Fact]
  public async Task AReferenceEscapingTheWorkspaceIsRefusedWithoutReading()
  {
    FakeFiles files = new();
    ReferenceInjectionResult r = await Injector(files).InjectAsync("@../outside.txt", Ct);
    Assert.False(r.IsSuccess);
    Assert.Contains("ReferenceOutsideWorkspace", r.FailureNotice, StringComparison.Ordinal);
    Assert.Empty(files.Reads);
  }

  [Fact]
  public async Task EveryReferenceInTheMessageAppendsInOrder()
  {
    ReferenceInjectionResult r = await Injector(new FakeFiles()).InjectAsync("@a.txt and @b.txt", Ct);
    Assert.True(r.IsSuccess);
    Assert.Equal(2, r.AppendedBlocks.Count);
    Assert.Contains("a.txt", r.AppendedBlocks[0], StringComparison.Ordinal);
    Assert.Contains("b.txt", r.AppendedBlocks[1], StringComparison.Ordinal);
    Assert.Equal(2, r.ReferenceCount);
  }

  [Fact]
  public async Task AFileReferenceReadsOneReadToolPage()
  {
    FakeFiles files = new();
    ReferenceInjector injector = new(files, null, Root);
    _ = await injector.InjectAsync("@notes.txt", Ct);
    Assert.Equal(Path.Combine(Path.GetFullPath(Root), "notes.txt"), Assert.Single(files.Reads));
  }

  [Fact]
  public async Task SkillReferenceAppendsTheInvokedSkillLine()
  {
    SkillInvocationService skills = new(new FakeCatalog(Skill("grill")));
    ReferenceInjectionResult r = await Injector(new FakeFiles(), skills: skills).InjectAsync("@grill", Ct);
    Assert.True(r.IsSuccess);
    Assert.Contains("[skill invoked: grill]", r.AppendedBlocks[0], StringComparison.Ordinal);
    Assert.Contains("Body of grill", r.AppendedBlocks[0], StringComparison.Ordinal);
  }

  [Fact]
  public async Task ASkillNameWinsOverAFileOfTheSameName()
  {
    FakeFiles files = new();
    SkillInvocationService skills = new(new FakeCatalog(Skill("notes.txt")));
    ReferenceInjectionResult r = await Injector(files, skills: skills).InjectAsync("@notes.txt", Ct);
    Assert.True(r.IsSuccess);
    Assert.Contains("[skill invoked: notes.txt]", r.AppendedBlocks[0], StringComparison.Ordinal);
    Assert.Empty(files.Reads);
  }

  [Fact]
  public async Task AnUnknownSkillNameFallsThroughToTheFileCheck()
  {
    SkillInvocationService skills = new(new FakeCatalog(Skill("grill")));
    ReferenceInjectionResult r = await Injector(new FakeFiles { Missing = true }, skills: skills)
        .InjectAsync("@alpha", Ct);
    Assert.False(r.IsSuccess);
    Assert.Contains("ReferenceNotFound", r.FailureNotice, StringComparison.Ordinal);
    Assert.Contains("alpha", r.FailureNotice, StringComparison.Ordinal);
  }

  [Fact]
  public async Task AnEscapedAtIsNeverResolved()
  {
    FakeFiles files = new();
    ReferenceInjectionResult r = await Injector(files).InjectAsync("mail a@@b.txt please", Ct);
    Assert.True(r.IsSuccess);
    Assert.Empty(r.AppendedBlocks);
    Assert.Equal("mail a@b.txt please", r.MessageText);
    Assert.Empty(files.Reads);
  }

  [Fact]
  public async Task NoReferencesLeavesTheMessageUnchanged()
  {
    ReferenceInjectionResult r = await Injector(new FakeFiles()).InjectAsync("plain text", Ct);
    Assert.True(r.IsSuccess);
    Assert.Equal("plain text", r.MessageText);
    Assert.Empty(r.AppendedBlocks);
    Assert.Null(r.FailureNotice);
    Assert.Equal(0, r.ReferenceCount);
    Assert.Equal(0, r.EstimatedTokens);
  }

  [Fact]
  public async Task EstimatedTokensIsTheAppendedCharactersOverFour()
  {
    ReferenceInjectionResult r = await Injector(new FakeFiles()).InjectAsync("@notes.txt", Ct);
    int characters = r.AppendedBlocks.Sum(b => b.Length);
    Assert.Equal(characters / 4, r.EstimatedTokens);
  }
}
