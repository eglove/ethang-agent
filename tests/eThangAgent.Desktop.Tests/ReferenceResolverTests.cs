using eThangAgent.Agent.Application;
using eThangAgent.Desktop.ViewModels;
using eThangAgent.SharedKernel;
using eThangAgent.SkillDomain;
using eThangAgent.ToolDomain;

namespace eThangAgent.Desktop.Tests;

public class ReferenceResolverTests
{
  private const string Root = "C:/w";

  private static CancellationToken Ct => TestContext.Current.CancellationToken;

  private sealed class FakeCatalog(params SkillDefinition[] skills) : ISkillCatalog
  {
    public Task<Result<IReadOnlyList<SkillDefinition>>> ListAsync(CancellationToken ct = default) =>
        Task.FromResult(Result.Success<IReadOnlyList<SkillDefinition>>([.. skills]));

    public Task<Result<SkillDefinition>> GetAsync(string name, CancellationToken ct = default) =>
        Task.FromResult(Result.Failure<SkillDefinition>(new DomainError("SkillNotFound", name)));
  }

  private static SkillDefinition Skill(string name) => new(
      name, "Does " + name + " things", "Body of " + name, 1, SkillSource.BuiltIn);

  private static ReferenceResolver Resolver(SkillInvocationService? skills = null,
      SessionPathGrants? grants = null)
    => new(skills, Root, grants);

  [Fact]
  public async Task AFileReferenceIsNamedByPathAndNothingIsRead()
  {
    // A reference is a POINTER, not a fetch: the resolver names the path and stops, because
    // whether the model needs the contents is the model's judgement.
    ReferenceResolution r = await Resolver().ResolveAsync("check @notes.txt", Ct);

    Assert.Empty(r.Notices);
    Assert.Empty(r.Blocks);
    Assert.Equal("check @notes.txt", r.MessageText);
    Assert.Contains("@notes.txt", r.Annotation, StringComparison.Ordinal);
    Assert.Contains("notes.txt", r.Annotation, StringComparison.Ordinal);
  }

  [Fact]
  public async Task TheAnnotationSaysReadingIsTheModelsCall()
  {
    // The message carries no file content, so the annotation must not read as an instruction
    // to read: a model that already holds the file would spend a turn re-fetching it.
    ReferenceResolution r = await Resolver().ResolveAsync("@notes.txt", Ct);

    Assert.Contains("[references]", r.Annotation, StringComparison.Ordinal);
    Assert.Contains("read one only if you need its contents", r.Annotation, StringComparison.Ordinal);
    Assert.Contains("may already be in your context", r.Annotation, StringComparison.Ordinal);
  }

  [Fact]
  public async Task EveryFileReferenceIsNamedInOrder()
  {
    ReferenceResolution r = await Resolver().ResolveAsync("@a.txt and @b.txt", Ct);

    Assert.Empty(r.Notices);
    Assert.Contains("2 paths", r.Annotation, StringComparison.Ordinal);
    int first = r.Annotation.IndexOf("@a.txt", StringComparison.Ordinal);
    int second = r.Annotation.IndexOf("@b.txt", StringComparison.Ordinal);
    Assert.True(first >= 0 && second > first, $"a.txt at {first}, b.txt at {second}");
  }

  [Fact]
  public async Task AReferenceEscapingTheWorkspacePostsANoticeAndNamesNothing()
  {
    ReferenceResolution r = await Resolver().ResolveAsync("@../outside.txt", Ct);

    Assert.Contains("ReferenceOutsideWorkspace", Assert.Single(r.Notices), StringComparison.Ordinal);
    Assert.Equal(string.Empty, r.Annotation);
  }

  [Fact]
  public async Task AMissingFileIsNamedAnyway()
  {
    // Nothing is read, so nothing can fail on absence: the annotation names the path the
    // reference resolved to, and the read tool is where a missing file surfaces.
    ReferenceResolution r = await Resolver().ResolveAsync("look at @nope/Missing.cs", Ct);

    Assert.Empty(r.Notices);
    Assert.Contains("@nope/Missing.cs", r.Annotation, StringComparison.Ordinal);
  }

  [Fact]
  public async Task AQuotedPathIsGrantedAndNamedAsGranted()
  {
    SessionPathGrants grants = new();
    ReferenceResolution r = await Resolver(grants: grants).ResolveAsync("@\"C:\\vault\\a.txt\"", Ct);

    Assert.Empty(r.Notices);
    Assert.Contains("granted read-only", r.Annotation, StringComparison.Ordinal);
    Assert.Contains("C:\\vault\\a.txt", Assert.Single(grants.List).Root, StringComparison.Ordinal);
  }

  [Fact]
  public async Task SkillReferenceAppendsTheInvokedSkillLine()
  {
    SkillInvocationService skills = new(new FakeCatalog(Skill("grill")));
    ReferenceResolution r = await Resolver(skills).ResolveAsync("@grill", Ct);

    Assert.Empty(r.Notices);
    Assert.Contains("[skill invoked: grill]", Assert.Single(r.Blocks).Content, StringComparison.Ordinal);
    Assert.Equal(string.Empty, r.Annotation);
  }

  [Fact]
  public async Task ASkillNameWinsOverAFileOfTheSameName()
  {
    SkillInvocationService skills = new(new FakeCatalog(Skill("notes.txt")));
    ReferenceResolution r = await Resolver(skills).ResolveAsync("@notes.txt", Ct);

    Assert.Contains("[skill invoked: notes.txt]", Assert.Single(r.Blocks).Content, StringComparison.Ordinal);
    Assert.Equal(string.Empty, r.Annotation);
  }

  [Fact]
  public async Task AnUnknownSkillNameFallsThroughToTheFileCheck()
  {
    SkillInvocationService skills = new(new FakeCatalog(Skill("grill")));
    ReferenceResolution r = await Resolver(skills).ResolveAsync("@alpha", Ct);

    Assert.Empty(r.Notices);
    Assert.Contains("@alpha", r.Annotation, StringComparison.Ordinal);
  }

  [Fact]
  public async Task AnEscapedAtIsNeverResolved()
  {
    ReferenceResolution r = await Resolver().ResolveAsync("mail a@@b.txt please", Ct);

    Assert.Empty(r.Notices);
    Assert.Empty(r.Blocks);
    Assert.Equal("mail a@b.txt please", r.MessageText);
    Assert.Equal(string.Empty, r.Annotation);
  }

  [Fact]
  public async Task NoReferencesLeavesTheMessageUnchanged()
  {
    ReferenceResolution r = await Resolver().ResolveAsync("plain text", Ct);

    Assert.Equal("plain text", r.MessageText);
    Assert.Empty(r.Blocks);
    Assert.Empty(r.Notices);
    Assert.Equal(string.Empty, r.Annotation);
  }

  [Fact]
  public void NamesAreTheUnescapedTokensWithoutTheirTrigger()
  {
    Assert.Equal(["a.txt", "b.txt"], ReferenceResolver.Names("@a.txt and @b.txt"));
    Assert.Empty(ReferenceResolver.Names("mail me@@example.com"));
    Assert.Empty(ReferenceResolver.Names("no references here"));
  }
}
