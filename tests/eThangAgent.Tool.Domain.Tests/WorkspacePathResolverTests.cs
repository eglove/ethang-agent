using eThangAgent.SharedKernel;

namespace eThangAgent.ToolDomain.Tests;

public sealed class WorkspacePathResolverTests : IDisposable
{
  private readonly string _root = Directory.CreateTempSubdirectory("ethang-ws").FullName;
  private WorkspacePathResolver MakeResolver() => new(_root);

  public void Dispose()
  {
    try
    {
      Directory.Delete(_root, recursive: true);
    }
    catch (IOException)
    {
      // best-effort teardown of the temp workspace.
    }
    catch (UnauthorizedAccessException)
    {
      // best-effort teardown of the temp workspace.
    }

    GC.SuppressFinalize(this);
  }

  [Fact]
  public void RelativePath_ResolvesAgainstRoot()
  {
    Result<string> r = MakeResolver().Resolve("src\\a.cs");
    Assert.True(r.IsSuccess);
    Assert.Equal(Path.Combine(_root, "src", "a.cs"), r.Value);
  }

  [Fact]
  public void DotSegments_Collapse()
  {
    Result<string> r = MakeResolver().Resolve("src\\.\\b.cs");
    Assert.True(r.IsSuccess);
    Assert.Equal(Path.Combine(_root, "src", "b.cs"), r.Value);
  }

  [Fact]
  public void AbsolutePathInsideRoot_Accepted()
  {
    string p = Path.Combine(_root, "c.cs");
    Result<string> r = MakeResolver().Resolve(p);
    Assert.True(r.IsSuccess);
    Assert.Equal(p, r.Value);
  }

  [Fact]
  public void ParentEscape_Rejected()
  {
    Result<string> r = MakeResolver().Resolve("..\\outside.txt");
    Assert.False(r.IsSuccess);
    Assert.Equal("PathOutsideWorkspace", r.Error!.Code);
    Assert.Contains(_root, r.Error.Message, StringComparison.Ordinal);
  }

  [Fact]
  public void SiblingPrefixEscape_Rejected()
  {
    // A sibling directory whose name shares a prefix with the root starts
    // with _root as a raw string but is outside it — comparison must be segment-aware.
    string sibling = _root.TrimEnd('\\') + "x\\file.txt";
    Result<string> r = MakeResolver().Resolve(sibling);
    Assert.False(r.IsSuccess);
    Assert.Equal("PathOutsideWorkspace", r.Error!.Code);
  }

  [Fact]
  public void RootItself_Accepted()
  {
    Result<string> r = MakeResolver().Resolve(_root);
    Assert.True(r.IsSuccess);
    Assert.Equal(Path.GetFullPath(_root), r.Value);
  }

  [Theory]
  [InlineData("")]
  [InlineData("   ")]
  public void EmptyOrWhitespace_Rejected(string path)
  {
    Result<string> r = MakeResolver().Resolve(path);
    Assert.False(r.IsSuccess);
    Assert.Equal("InvalidPath", r.Error!.Code);
  }

  // ── Trailing-separator normalization (regression: folder-picker roots carry a
  //    trailing '\'; resolved candidates never do, so equal-root containment failed) ──

  [Fact]
  public void RootWithTrailingSeparator_DotPath_Accepted()
  {
    Result<string> r = new WorkspacePathResolver(_root + Path.DirectorySeparatorChar).Resolve(".");
    Assert.True(r.IsSuccess,
        $"'.' must resolve inside a workspace whose root carries a trailing separator; got: {r.Error?.Code} {r.Error?.Message}");
    Assert.Equal(Path.GetFullPath(_root), r.Value);
  }

  [Fact]
  public void RootWithTrailingSeparator_RelativeSubpath_Accepted()
  {
    Result<string> r = new WorkspacePathResolver(_root + Path.DirectorySeparatorChar)
            .Resolve("docs" + Path.DirectorySeparatorChar + "a.md");
    Assert.True(r.IsSuccess);
    Assert.Equal(Path.Combine(_root, "docs", "a.md"), r.Value);
  }

  [Fact]
  public void RootWithoutTrailingSeparator_RootItself_Accepted()
  {
    Result<string> r = MakeResolver().Resolve(_root);
    Assert.True(r.IsSuccess);
    Assert.Equal(Path.GetFullPath(_root), r.Value);
  }

  [Fact]
  public void DriveLetterCaseDifference_InsideRoot_Accepted()
  {
    if (_root.Length < 2 || _root[1] != ':')
    {
      return; // not a drive-letter path
    }

    string altCase = char.ToUpperInvariant(_root[0]) + _root[1..];
    if (altCase == _root)
    {
      altCase = char.ToLowerInvariant(_root[0]) + _root[1..];
    }

    Result<string> r = MakeResolver().Resolve(Path.Combine(altCase, "c.cs"));
    Assert.True(r.IsSuccess,
        $"drive-letter casing differs from the stored root but points inside it; got: {r.Error?.Code}");
  }

  [Fact]
  public void RootWithTrailingSeparator_EscapeStillRejected()
  {
    Result<string> r = new WorkspacePathResolver(_root + Path.DirectorySeparatorChar)
            .Resolve(".." + Path.DirectorySeparatorChar + "outside.txt");
    Assert.False(r.IsSuccess);
    Assert.Equal("PathOutsideWorkspace", r.Error!.Code);
  }
}
