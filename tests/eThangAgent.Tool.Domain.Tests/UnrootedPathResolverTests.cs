using eThangAgent.ToolDomain;

namespace eThangAgent.Tool.Domain.Tests;

public class UnrootedPathResolverTests
{
    private readonly UnrootedPathResolver _resolver = new();

    [Theory]
    [InlineData("C:\\work\\a.txt")]
    [InlineData("D:\\deep\\dir\\note.md")]
    public void Absolute_Paths_Pass_Through_Normalized(string path)
    {
        var result = _resolver.Resolve(path);
        Assert.True(result.IsSuccess);
        Assert.Equal(System.IO.Path.GetFullPath(path), result.Value);
    }

    [Fact]
    public void Relative_Paths_Resolve_Against_Process_Cwd()
    {
        var result = _resolver.Resolve("src\\file.cs");
        Assert.True(result.IsSuccess);
        Assert.Equal(Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), "src\\file.cs")), result.Value);
    }

    [Fact]
    public void Traversal_Is_Never_Rejected_As_Outside_Anything()
    {
        var result = _resolver.Resolve("..\\other\\file.txt");
        Assert.True(result.IsSuccess);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Empty_Or_Whitespace_Fails_InvalidPath(string path)
    {
        var result = _resolver.Resolve(path);
        Assert.False(result.IsSuccess);
        Assert.Equal("InvalidPath", result.Error!.Code);
    }

    [Fact]
    public void Malformed_Path_Fails_InvalidPath_Not_Exception()
    {
        var result = _resolver.Resolve("C:\\bad\\|<>\"");
        Assert.False(result.IsSuccess);
        Assert.Equal("InvalidPath", result.Error!.Code);
    }
}
