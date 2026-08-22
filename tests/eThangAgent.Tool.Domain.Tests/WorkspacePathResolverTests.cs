using eThangAgent.SharedKernel;
using eThangAgent.ToolDomain;

namespace eThangAgent.ToolDomain.Tests;

public class WorkspacePathResolverTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("ethang-ws").FullName;
    private WorkspacePathResolver MakeResolver() => new(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public void RelativePath_ResolvesAgainstRoot()
    {
        var r = MakeResolver().Resolve("src\\a.cs");
        Assert.True(r.IsSuccess);
        Assert.Equal(Path.Combine(_root, "src", "a.cs"), r.Value);
    }

    [Fact]
    public void DotSegments_Collapse()
    {
        var r = MakeResolver().Resolve("src\\.\\b.cs");
        Assert.True(r.IsSuccess);
        Assert.Equal(Path.Combine(_root, "src", "b.cs"), r.Value);
    }

    [Fact]
    public void AbsolutePathInsideRoot_Accepted()
    {
        var p = Path.Combine(_root, "c.cs");
        var r = MakeResolver().Resolve(p);
        Assert.True(r.IsSuccess);
        Assert.Equal(p, r.Value);
    }

    [Fact]
    public void ParentEscape_Rejected()
    {
        var r = MakeResolver().Resolve("..\\outside.txt");
        Assert.False(r.IsSuccess);
        Assert.Equal("PathOutsideWorkspace", r.Error!.Code);
        Assert.Contains(_root, r.Error.Message);
    }

    [Fact]
    public void SiblingPrefixEscape_Rejected()
    {
        // A sibling directory whose name shares a prefix with the root starts
        // with _root as a raw string but is outside it — comparison must be segment-aware.
        var sibling = _root.TrimEnd('\\') + "x\\file.txt";
        var r = MakeResolver().Resolve(sibling);
        Assert.False(r.IsSuccess);
        Assert.Equal("PathOutsideWorkspace", r.Error!.Code);
    }

    [Fact]
    public void RootItself_Accepted()
    {
        var r = MakeResolver().Resolve(_root);
        Assert.True(r.IsSuccess);
        Assert.Equal(Path.GetFullPath(_root), r.Value);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void EmptyOrWhitespace_Rejected(string path)
    {
        var r = MakeResolver().Resolve(path);
        Assert.False(r.IsSuccess);
        Assert.Equal("InvalidPath", r.Error!.Code);
    }
}
