using eThangAgent.SharedKernel;

namespace eThangAgent.ToolDomain.Tests;

/// <summary>ReadTool's path policy: relative paths resolve against the injected
/// resolver's root (containment enforced), absolute paths are accepted from
/// anywhere on disk — read is read-only. RootedAt carries the SAME policy onto
/// the anchor: a child anchored at a worktree keeps the policy over that anchor,
/// so its relative paths stay confined while its absolute paths still reach the
/// wider disk.</summary>
public sealed class ReadToolPathPolicyTests
{
  private static FileRead Ok() => new(["content"], 1, 1);

  [Fact]
  public async Task RelativePath_ResolvesAgainstRoot()
  {
    ReadTool tool = new(new WorkspacePathResolver("C:\\ws"), new FakeFiles(Result.Success(Ok())));
    ToolResult result = await tool.ExecuteAsync(NewCall("src\\a.cs"), ct: TestContext.Current.CancellationToken);
    Assert.False(result.IsError);
    Assert.StartsWith("[read C:\\ws\\src\\a.cs ", result.Content, StringComparison.Ordinal);
  }

  [Fact]
  public async Task RelativeEscape_Rejected()
  {
    ReadTool tool = new(new WorkspacePathResolver("C:\\ws"), new FakeFiles(Result.Success(Ok())));
    ToolResult result = await tool.ExecuteAsync(NewCall("..\\outside.txt"), ct: TestContext.Current.CancellationToken);
    Assert.True(result.IsError);
    Assert.Contains("PathOutsideWorkspace", result.Content, StringComparison.Ordinal);
  }

  [Fact]
  public async Task AbsoluteOutsideRoot_Accepted()
  {
    ReadTool tool = new(new WorkspacePathResolver("C:\\ws"), new FakeFiles(Result.Success(Ok())));
    ToolResult result = await tool.ExecuteAsync(NewCall("C:\\elsewhere\\file.txt"), ct: TestContext.Current.CancellationToken);
    Assert.False(result.IsError);
    Assert.StartsWith("[read C:\\elsewhere\\file.txt ", result.Content, StringComparison.Ordinal);
  }

  [Fact]
  public async Task RootedAt_RelativePath_ResolvesAgainstAnchor()
  {
    ReadTool rooted = (ReadTool)new ReadTool(
        new WorkspacePathResolver("C:\\ws"), new FakeFiles(Result.Success(Ok()))).RootedAt("C:\\anchor");
    ToolResult result = await rooted.ExecuteAsync(NewCall("src\\a.cs"), ct: TestContext.Current.CancellationToken);
    Assert.False(result.IsError);
    Assert.StartsWith("[read C:\\anchor\\src\\a.cs ", result.Content, StringComparison.Ordinal);
  }

  [Fact]
  public async Task RootedAt_RelativeEscape_StillRejected()
  {
    ReadTool rooted = (ReadTool)new ReadTool(
        new WorkspacePathResolver("C:\\ws"), new FakeFiles(Result.Success(Ok()))).RootedAt("C:\\anchor");
    ToolResult result = await rooted.ExecuteAsync(NewCall("..\\outside.txt"), ct: TestContext.Current.CancellationToken);
    Assert.True(result.IsError);
    Assert.Contains("PathOutsideWorkspace", result.Content, StringComparison.Ordinal);
  }

  [Fact]
  public async Task RootedAt_AbsoluteOutsideAnchor_Accepted()
  {
    ReadTool rooted = (ReadTool)new ReadTool(
        new WorkspacePathResolver("C:\\ws"), new FakeFiles(Result.Success(Ok()))).RootedAt("C:\\anchor");
    ToolResult result = await rooted.ExecuteAsync(NewCall("C:\\elsewhere\\f.txt"), ct: TestContext.Current.CancellationToken);
    Assert.False(result.IsError);
    Assert.StartsWith("[read C:\\elsewhere\\f.txt ", result.Content, StringComparison.Ordinal);
  }

  private static RawToolInput NewCall(string path)
  {
    string json = System.Text.Json.JsonSerializer.Serialize(
        new { timeoutSeconds = 120, path, startLine = 1, endLine = 1 });
    return new RawToolInput("read", json);
  }

  private sealed class FakeFiles(Result<FileRead> result) : IFileSystemAccess
  {
    public Task<Result<byte[]>> ReadBytesAsync(string path, CancellationToken ct = default) => throw new NotImplementedException();
    public Task<Result<FileRead>> ReadLinesAsync(string path, int startLine, int endLine, CancellationToken ct = default)
        => Task.FromResult(result);
  }
}
