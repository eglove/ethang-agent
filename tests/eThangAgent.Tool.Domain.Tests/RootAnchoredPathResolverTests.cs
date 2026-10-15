using eThangAgent.SharedKernel;

namespace eThangAgent.ToolDomain.Tests;

/// <summary>The read tool's path policy: relative paths stay bound to the wrapped
/// resolver's root (a workspace root in production — containment enforced, so a
/// relative path can never leave the workspace), while absolute paths are accepted
/// from anywhere on disk. Read is read-only, so an absolute path needs no
/// confinement; mutating tools keep the strict WorkspacePathResolver.</summary>
public sealed class RootAnchoredPathResolverTests
{
  private static RootAnchoredPathResolver Make() =>
      new(new WorkspacePathResolver("C:\\ws"));

  [Fact]
  public void RelativePath_ResolvesAgainstRoot()
  {
    Result<string> r = Make().Resolve("src\\a.cs");
    Assert.True(r.IsSuccess);
    Assert.Equal("C:\\ws\\src\\a.cs", r.Value);
  }

  [Fact]
  public void RelativeEscape_Rejected()
  {
    // Relative paths remain bound to the workspace root: '..' may not escape it.
    Result<string> r = Make().Resolve("..\\outside.txt");
    Assert.False(r.IsSuccess);
    Assert.Equal("PathOutsideWorkspace", r.Error.Code);
  }

  [Fact]
  public void AbsoluteInsideRoot_Accepted()
  {
    Result<string> r = Make().Resolve("C:\\ws\\c.cs");
    Assert.True(r.IsSuccess);
    Assert.Equal("C:\\ws\\c.cs", r.Value);
  }

  [Fact]
  public void AbsoluteOutsideRoot_Accepted()
  {
    // The policy change: an absolute path may point anywhere on disk.
    Result<string> r = Make().Resolve("C:\\elsewhere\\file.txt");
    Assert.True(r.IsSuccess);
    Assert.Equal("C:\\elsewhere\\file.txt", r.Value);
  }

  [Fact]
  public void EmptyPath_Rejected()
  {
    Result<string> r = Make().Resolve("  ");
    Assert.False(r.IsSuccess);
    Assert.Equal("InvalidPath", r.Error.Code);
  }

  [Fact]
  public void AbsolutePathWithInvalidCharacters_Rejected()
  {
    Result<string> r = Make().Resolve("C:\\ba|d.txt");
    Assert.False(r.IsSuccess);
    Assert.Equal("InvalidPath", r.Error.Code);
  }
}
