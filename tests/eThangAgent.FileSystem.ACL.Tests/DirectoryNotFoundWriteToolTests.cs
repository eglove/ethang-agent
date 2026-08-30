using System.Text.Json;
using eThangAgent.SharedKernel;
using eThangAgent.ToolDomain;

namespace eThangAgent.FileSystem.ACL.Tests;

/// <summary>Pins the end-to-end contract of the improved DirectoryNotFound error:
/// the write tool surfaces the ACL's actionable message naming the missing parent
/// directory and the remedy, so the model can self-correct without a round-trip
/// to the documentation.</summary>
public sealed class DirectoryNotFoundWriteToolTests
{
  [Fact]
  public async Task WriteTool_WithMissingParentDirectory_NamesDirectoryAndRemedy()
  {
    string root = Directory.CreateTempSubdirectory("ethang-dnf").FullName;
    try
    {
      using DirectFileSystemAccess access = new();
      WriteTool tool = new(new WorkspacePathResolver(root), access);

      string missing = Path.Combine(root, "no", "such");
      ToolResult result = await tool.ExecuteAsync(new RawToolInput("write",
        JsonSerializer.Serialize(new
        {
          timeoutSeconds = 30,
          path = Path.Combine("no", "such", "f.txt"),
          content = "x"
        })), ct: TestContext.Current.CancellationToken);

      Assert.True(result.IsError);
      Assert.Contains("DirectoryNotFound", result.Content, StringComparison.Ordinal);
      Assert.Contains(missing, result.Content, StringComparison.Ordinal);
      Assert.Contains("Create it first", result.Content, StringComparison.Ordinal);
    }
    finally
    {
      TryCleanup(root);
    }
  }

  [Fact]
  public async Task WriteFileBytesAsync_WithMissingParentDirectory_NamesDirectoryAndRemedy()
  {
    string root = Directory.CreateTempSubdirectory("ethang-dnfb").FullName;
    try
    {
      using DirectFileSystemAccess access = new();

      string missing = Path.Combine(root, "no", "such");
      Result<FileWriteOutcome> r = await access.WriteFileBytesAsync(
        Path.Combine(missing, "f.bin"), [0x62, 0x79], overwrite: false, ct: TestContext.Current.CancellationToken);

      Assert.False(r.IsSuccess);
      Assert.Equal("DirectoryNotFound", r.Error.Code);
      Assert.Contains(missing, r.Error.Message, StringComparison.Ordinal);
      Assert.Contains("Create it first", r.Error.Message, StringComparison.Ordinal);
    }
    finally
    {
      TryCleanup(root);
    }
  }

  private static void TryCleanup(string root)
  {
    try
    {
      Directory.Delete(root, recursive: true);
    }
    catch (IOException)
    {
      // best-effort temp cleanup
    }
    catch (UnauthorizedAccessException)
    {
      // best-effort temp cleanup
    }
  }
}