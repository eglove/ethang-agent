using eThangAgent.FileSystem.ACL;
using eThangAgent.SharedKernel;

namespace eThangAgent.FileSystem.ACL.Tests;

public sealed class FileWriteIntegrationTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("ethang-w").FullName;
    private readonly PowerShellFileSystemAccess _access = new();

    public void Dispose()
    {
        _access.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public async Task WriteNewFile_Succeeds_CreatedTrue()
    {
        var path = Path.Combine(_root, "new.txt");
        var r = await _access.WriteFileAsync(path, "hello", overwrite: false);
        Assert.True(r.IsSuccess);
        Assert.True(r.Value!.Created);
        Assert.Equal("hello", File.ReadAllText(path));
    }

    [Fact]
    public async Task WriteExisting_WithoutOverwrite_Fails_FileExists()
    {
        var path = Path.Combine(_root, "x.txt");
        await _access.WriteFileAsync(path, "first", overwrite: false);
        var r = await _access.WriteFileAsync(path, "second", overwrite: false);
        Assert.False(r.IsSuccess);
        Assert.Equal("FileExists", r.Error!.Code);
        Assert.Equal("first", File.ReadAllText(path)); // unchanged
    }

    [Fact]
    public async Task WriteExisting_WithOverwrite_ReplacesContent_CreatedFalse()
    {
        var path = Path.Combine(_root, "y.txt");
        await _access.WriteFileAsync(path, "old", overwrite: false);
        var r = await _access.WriteFileAsync(path, "brand new content", overwrite: true);
        Assert.True(r.IsSuccess);
        Assert.False(r.Value!.Created);
        Assert.Equal("brand new content", File.ReadAllText(path));
    }

    [Fact]
    public async Task BytesWritten_ReflectsUtf8ByteCount_NoBom()
    {
        var path = Path.Combine(_root, "bytes.txt");
        var r = await _access.WriteFileAsync(path, "\u00e9", overwrite: false); // \u00e9 = 2 UTF-8 bytes
        Assert.True(r.IsSuccess);
        Assert.Equal(2L, r.Value!.BytesWritten);
        var raw = File.ReadAllBytes(path);
        Assert.NotEqual(0xEF, raw[0]); // no BOM
    }

    [Fact]
    public async Task MissingParentDirectory_Fails_DirectoryNotFound()
    {
        var path = Path.Combine(_root, "no", "such", "dir", "f.txt");
        var r = await _access.WriteFileAsync(path, "x", overwrite: false);
        Assert.False(r.IsSuccess);
        Assert.Equal("DirectoryNotFound", r.Error!.Code);
    }
}
