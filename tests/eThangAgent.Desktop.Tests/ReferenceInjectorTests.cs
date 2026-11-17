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

    /// <summary>File names that are absent while every other name resolves, so one message can
    ///     carry a resolvable reference beside an absent one.</summary>
    public IReadOnlyList<string> MissingFor { get; init; } = [];

    public Task<Result<FileRead>> ReadLinesAsync(string path, int startLine, int endLine, CancellationToken ct = default)
    {
      Reads.Add(path);
      bool absent = Missing || MissingFor.Contains(Path.GetFileName(path), StringComparer.OrdinalIgnoreCase);
      return Task.FromResult(absent
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

  /// <summary>An injector with the four read tools wired, which is what a session has. The
  ///     tools answer for the file's KIND, so a test asserts on the tool a reference chose and
  ///     the arguments it was given.</summary>
  private static ReferenceInjector Injector(IFileSystemAccess files, SkillInvocationService? skills = null,
      params ITool[] tools)
    => new(files, skills, Root, tools: ReferenceToolSurface.Over(tools.Length == 0
        ? [ReferenceToolSurface.Text(files)]
        : tools));

  [Fact]
  public async Task AMissingFilePostsANoticeAndDeliversNothing()
  {
    // A reference that resolves to nothing is information about the message, not a reason to
    // withhold it: the notice names the path and the message travels without it.
    ReferenceInjectionResult r = await Injector(new FakeFiles { Missing = true })
        .InjectAsync("look at @nope/Missing.cs", Ct);
    Assert.Equal("look at @nope/Missing.cs", r.MessageText);
    Assert.Empty(r.AppendedBlocks);
    Assert.Contains("ReferenceUnreadable", Assert.Single(r.Notices), StringComparison.Ordinal);
    Assert.Contains("nope/Missing.cs", r.Notices[0], StringComparison.Ordinal);
    Assert.Equal(0, r.ReferenceCount);
  }

  [Fact]
  public async Task AResolvedReferenceBesideAMissingOneStillArrives()
  {
    // One bad reference must not cost the user the good one.
    FakeFiles files = new() { MissingFor = ["gone.txt"] };
    ReferenceInjectionResult r = await Injector(files).InjectAsync("@notes.txt and @gone.txt", Ct);
    Assert.Contains("1→ alpha", Assert.Single(r.AppendedBlocks).Content, StringComparison.Ordinal);
    _ = Assert.Single(r.Notices);
    Assert.Equal(1, r.ReferenceCount);
  }

  [Fact]
  public async Task FileReferenceAppendsTheReadToolsOwnOutput()
  {
    ReferenceInjectionResult r = await Injector(new FakeFiles()).InjectAsync("check @notes.txt", Ct);
    Assert.Empty(r.Notices);
    Assert.Equal("check @notes.txt", r.MessageText);
    ReferenceBlock block = Assert.Single(r.AppendedBlocks);
    Assert.Equal("read", block.ToolName, StringComparer.Ordinal);
    Assert.StartsWith("[read ", block.Content, StringComparison.Ordinal);
    Assert.Contains("1→ alpha", block.Content, StringComparison.Ordinal);
    Assert.Contains("2→ beta", block.Content, StringComparison.Ordinal);
  }

  [Fact]
  public async Task ATextReferenceAsksTheReadToolForOnePage()
  {
    // The reference is a READ, so it asks the read tool the way the model would: the file,
    // the first page, and a stated budget.
    StubReadTool read = ReferenceToolSurface.Text(new FakeFiles());
    _ = await Injector(new FakeFiles(), null, read).InjectAsync("@notes.txt", Ct);
    Assert.Equal(1, read.LastArguments.GetProperty("startLine").GetInt32());
    Assert.Equal(1000, read.LastArguments.GetProperty("endLine").GetInt32());
    Assert.True(read.LastArguments.GetProperty("timeoutSeconds").GetInt32() >= 1);
    Assert.EndsWith("notes.txt", read.LastArguments.GetProperty("path").GetString()!, StringComparison.Ordinal);
  }

  [Fact]
  public async Task AnImageReferenceReadsThroughReadImageAndKeepsTheImage()
  {
    // A picture referenced in a message is a picture the model must SEE: the block carries the
    // bytes read_image attached, and its text says what was attached.
    StubReadTool image = ReferenceToolSurface.Image();
    ReferenceInjectionResult r = await Injector(new FakeFiles(), null, image).InjectAsync("@shot.png", Ct);
    Assert.Empty(r.Notices);
    ReferenceBlock block = Assert.Single(r.AppendedBlocks);
    Assert.Equal("read_image", block.ToolName, StringComparer.Ordinal);
    Assert.Contains("[read_image ", block.Content, StringComparison.Ordinal);
    ToolResultImage attached = Assert.Single(block.Images);
    Assert.Equal("image/png", attached.MediaType, StringComparer.Ordinal);
    Assert.Contains("[image: image/png, 3 bytes]", block.ModelText(), StringComparison.Ordinal);
  }

  [Fact]
  public async Task APdfReferenceReadsThroughThePdfTool()
  {
    ReferenceInjectionResult r = await Injector(new FakeFiles(), null, ReferenceToolSurface.Pdf())
        .InjectAsync("@report.pdf", Ct);
    ReferenceBlock block = Assert.Single(r.AppendedBlocks);
    Assert.Equal("pdf", block.ToolName, StringComparer.Ordinal);
    Assert.Contains("quarterly figures", block.Content, StringComparison.Ordinal);
  }

  [Fact]
  public async Task ADocxReferenceReadsThroughTheDocxTool()
  {
    ReferenceInjectionResult r = await Injector(new FakeFiles(), null, ReferenceToolSurface.Docx())
        .InjectAsync("@contract.docx", Ct);
    ReferenceBlock block = Assert.Single(r.AppendedBlocks);
    Assert.Equal("docx", block.ToolName, StringComparer.Ordinal);
    Assert.Contains("contract terms", block.Content, StringComparison.Ordinal);
  }

  [Fact]
  public async Task AFileNoReadToolOpensIsRefusedWithoutReadingIt()
  {
    // An archive is not something the harness can show. Reading it as lines would deliver a
    // page of replacement characters, so the reference is refused by name instead.
    FakeFiles files = new();
    ReferenceInjectionResult r = await Injector(files).InjectAsync("@bundle.zip", Ct);
    Assert.Empty(r.AppendedBlocks);
    Assert.Contains("ReferenceUnreadable", Assert.Single(r.Notices), StringComparison.Ordinal);
    Assert.Contains("bundle.zip", r.Notices[0], StringComparison.Ordinal);
    Assert.Empty(files.Reads);
  }

  [Theory]
  [InlineData("notes.txt", "read")]
  [InlineData("Makefile", "read")]
  [InlineData("shot.png", "read_image")]
  [InlineData("shot.JPEG", "read_image")]
  [InlineData("report.pdf", "pdf")]
  [InlineData("contract.docx", "docx")]
  [InlineData("bundle.zip", null)]
  [InlineData("app.exe", null)]
  [InlineData("clip.mp3", null)]
  public void TheToolForAReferenceIsChosenByItsExtension(string name, string? expected)
      => Assert.Equal(expected, ReferenceInjector.ToolFor(name), StringComparer.Ordinal);

  [Fact]
  public async Task ARefusedReadBecomesTheReferencesNotice()
  {
    // The tool already named what was wrong with the file (its bytes are not that format), so
    // the notice carries that reason rather than a second guess at it.
    StubReadTool refusing = ReferenceToolSurface.Refusing("read_image",
        "Error [UnsupportedImageFormat]: 'fake.png' is not an image this tool can attach.");
    ReferenceInjectionResult r = await Injector(new FakeFiles(), null, refusing)
        .InjectAsync("@fake.png", Ct);
    Assert.Empty(r.AppendedBlocks);
    string notice = Assert.Single(r.Notices);
    Assert.Contains("ReferenceUnreadable", notice, StringComparison.Ordinal);
    Assert.Contains("UnsupportedImageFormat", notice, StringComparison.Ordinal);
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
        Assert.Contains("1→ alpha", block.Content, StringComparison.Ordinal));
  }

  [Fact]
  public async Task AReferenceEscapingTheWorkspaceIsRefusedWithoutReading()
  {
    FakeFiles files = new();
    ReferenceInjectionResult r = await Injector(files).InjectAsync("@../outside.txt", Ct);
    Assert.Contains("ReferenceOutsideWorkspace", Assert.Single(r.Notices), StringComparison.Ordinal);
    Assert.Empty(files.Reads);
  }

  [Fact]
  public async Task EveryReferenceInTheMessageAppendsInOrder()
  {
    ReferenceInjectionResult r = await Injector(new FakeFiles()).InjectAsync("@a.txt and @b.txt", Ct);
    Assert.Empty(r.Notices);
    Assert.Equal(2, r.AppendedBlocks.Count);
    Assert.Contains("a.txt", r.AppendedBlocks[0].Content, StringComparison.Ordinal);
    Assert.Contains("b.txt", r.AppendedBlocks[1].Content, StringComparison.Ordinal);
    Assert.Equal(2, r.ReferenceCount);
  }

  [Fact]
  public async Task SkillReferenceAppendsTheInvokedSkillLine()
  {
    SkillInvocationService skills = new(new FakeCatalog(Skill("grill")));
    ReferenceInjectionResult r = await Injector(new FakeFiles(), skills).InjectAsync("@grill", Ct);
    Assert.Empty(r.Notices);
    Assert.Contains("[skill invoked: grill]", r.AppendedBlocks[0].Content, StringComparison.Ordinal);
    Assert.Contains("Body of grill", r.AppendedBlocks[0].Content, StringComparison.Ordinal);
  }

  [Fact]
  public async Task ASkillNameWinsOverAFileOfTheSameName()
  {
    FakeFiles files = new();
    SkillInvocationService skills = new(new FakeCatalog(Skill("notes.txt")));
    ReferenceInjectionResult r = await Injector(files, skills).InjectAsync("@notes.txt", Ct);
    Assert.Empty(r.Notices);
    Assert.Contains("[skill invoked: notes.txt]", r.AppendedBlocks[0].Content, StringComparison.Ordinal);
    Assert.Empty(files.Reads);
  }

  [Fact]
  public async Task AnUnknownSkillNameFallsThroughToTheFileCheck()
  {
    SkillInvocationService skills = new(new FakeCatalog(Skill("grill")));
    ReferenceInjectionResult r = await Injector(new FakeFiles { Missing = true }, skills)
        .InjectAsync("@alpha", Ct);
    Assert.Contains("ReferenceUnreadable", Assert.Single(r.Notices), StringComparison.Ordinal);
    Assert.Contains("alpha", r.Notices[0], StringComparison.Ordinal);
  }

  [Fact]
  public async Task AnEscapedAtIsNeverResolved()
  {
    FakeFiles files = new();
    ReferenceInjectionResult r = await Injector(files).InjectAsync("mail a@@b.txt please", Ct);
    Assert.Empty(r.Notices);
    Assert.Empty(r.AppendedBlocks);
    Assert.Equal("mail a@b.txt please", r.MessageText);
    Assert.Empty(files.Reads);
  }

  [Fact]
  public async Task NoReferencesLeavesTheMessageUnchanged()
  {
    ReferenceInjectionResult r = await Injector(new FakeFiles()).InjectAsync("plain text", Ct);
    Assert.Equal("plain text", r.MessageText);
    Assert.Empty(r.AppendedBlocks);
    Assert.Empty(r.Notices);
    Assert.Equal(0, r.ReferenceCount);
    Assert.Equal(0, r.EstimatedTokens);
  }

  [Fact]
  public async Task EstimatedTokensIsTheAppendedCharactersOverFour()
  {
    ReferenceInjectionResult r = await Injector(new FakeFiles()).InjectAsync("@notes.txt", Ct);
    int characters = r.AppendedBlocks.Sum(b => b.EstimatedCharacters);
    Assert.Equal(characters / 4, r.EstimatedTokens);
  }

  [Fact]
  public async Task AnAttachedImageCountsTowardTheEstimate()
  {
    // An image has no text to count, and a readout that ignored it would call a referenced
    // screenshot free.
    ReferenceInjectionResult r = await Injector(new FakeFiles(), null, ReferenceToolSurface.Image())
        .InjectAsync("@shot.png", Ct);
    ReferenceBlock block = Assert.Single(r.AppendedBlocks);
    Assert.Equal(block.Content.Length + ReferenceInjector.CharactersPerImage, block.EstimatedCharacters);
  }
}
