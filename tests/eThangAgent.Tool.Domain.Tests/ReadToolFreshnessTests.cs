// JSON arguments in tool-input tests are plain string literals by design;
// the JSON-string analyzer's rewrites do not apply to RawToolInput payloads.
#pragma warning disable JSON002
using eThangAgent.SharedKernel;

namespace eThangAgent.ToolDomain.Tests;

/// <summary>T3: the read tool's freshness surface. Every versioned read's annotation
///     carries the token (B1); a re-read of a covered range at the same version
///     elides (B2) with force as the escape; a changed version re-reads with a
///     different token (B3); nothing ever fails a read (B4). A version-less read
///     (legacy fakes) stays byte-identical to the old output.</summary>
public class ReadToolFreshnessTests
{
  private static readonly DateTime Mtime = new(638000000000000000, DateTimeKind.Utc);
  private static readonly FileVersion V1 = new(100, Mtime);
  private static readonly FileVersion V2 = new(200, Mtime.AddSeconds(1));

  private static RawToolInput Args(string json) => new("read", json);

  private static ReadTool MakeTool(FileRead read, ReadFreshnessLedger? ledger = null)
      => new(new StubPathResolver(), new FixedFileSystemAccess(read), ledger);

  // ---- B1: token on every versioned read ----

  [Fact]
  public async Task FirstVersionedRead_AnnotationCarriesToken()
  {
    ReadTool tool = MakeTool(new FileRead(["alpha", "beta"], 2, 2, V1));
    ToolResult result = await tool.ExecuteAsync(Args("""{"timeoutSeconds":120,"path":"f","startLine":1,"endLine":2}"""), TestContext.Current.CancellationToken);
    Assert.False(result.IsError);
    Assert.StartsWith("[read f lines 1-2 of 2 total | " + V1.Token + "]", result.Content, StringComparison.Ordinal);
    Assert.Contains("1→ alpha", result.Content, StringComparison.Ordinal);
  }

  // ---- B2: covered re-read elides; force escapes ----

  [Fact]
  public async Task ReReadSameRange_ElidesContent()
  {
    ReadFreshnessLedger ledger = new();
    ReadTool tool = MakeTool(new FileRead(["alpha", "beta"], 2, 2, V1), ledger);

    ToolResult first = await tool.ExecuteAsync(Args("""{"timeoutSeconds":120,"path":"f","startLine":1,"endLine":2}"""), TestContext.Current.CancellationToken);
    ToolResult second = await tool.ExecuteAsync(Args("""{"timeoutSeconds":120,"path":"f","startLine":1,"endLine":2}"""), TestContext.Current.CancellationToken);

    Assert.False(first.IsError);
    Assert.False(second.IsError);
    Assert.Equal(
        "[read f lines 1-2 of 2 total | " + V1.Token + " | unchanged since your last read; content elided]",
        second.Content);
  }

  [Fact]
  public async Task ReReadWithForce_ReturnsFullContent()
  {
    ReadFreshnessLedger ledger = new();
    ReadTool tool = MakeTool(new FileRead(["alpha", "beta"], 2, 2, V1), ledger);

    _ = await tool.ExecuteAsync(Args("""{"timeoutSeconds":120,"path":"f","startLine":1,"endLine":2}"""), TestContext.Current.CancellationToken);
    ToolResult forced = await tool.ExecuteAsync(Args("""{"timeoutSeconds":120,"path":"f","startLine":1,"endLine":2,"force":true}"""), TestContext.Current.CancellationToken);

    Assert.False(forced.IsError);
    Assert.StartsWith("[read f lines 1-2 of 2 total | " + V1.Token + " | forced]", forced.Content, StringComparison.Ordinal);
    Assert.Contains("1→ alpha", forced.Content, StringComparison.Ordinal);
    Assert.Contains("2→ beta", forced.Content, StringComparison.Ordinal);
  }

  [Fact]
  public async Task FirstReadIsNeverElided()
  {
    ReadFreshnessLedger ledger = new();
    ReadTool tool = MakeTool(new FileRead(["alpha"], 1, 1, V1), ledger);

    ToolResult first = await tool.ExecuteAsync(Args("""{"timeoutSeconds":120,"path":"f","startLine":1,"endLine":1}"""), TestContext.Current.CancellationToken);

    Assert.False(first.IsError);
    Assert.Contains("1→ alpha", first.Content, StringComparison.Ordinal);
    Assert.DoesNotContain("elided", first.Content, StringComparison.Ordinal);
  }

  // ---- B3: changed version re-reads with a different token ----

  [Fact]
  public async Task ChangedVersion_ContentReturns_TokenDiffers()
  {
    ReadFreshnessLedger ledger = new();
    ScriptedFileSystemAccess files = new(
        new FileRead(["alpha", "beta"], 2, 2, V1),
        new FileRead(["alpha", "gamma", "delta"], 3, 3, V2));
    ReadTool tool = new(new StubPathResolver(), files, ledger);

    ToolResult first = await tool.ExecuteAsync(Args("""{"timeoutSeconds":120,"path":"f","startLine":1,"endLine":2}"""), TestContext.Current.CancellationToken);
    ToolResult second = await tool.ExecuteAsync(Args("""{"timeoutSeconds":120,"path":"f","startLine":1,"endLine":3}"""), TestContext.Current.CancellationToken);

    Assert.False(first.IsError);
    Assert.False(second.IsError);
    Assert.StartsWith("[read f lines 1-3 of 3 total | " + V2.Token + "]", second.Content, StringComparison.Ordinal);
    Assert.Contains("3→ delta", second.Content, StringComparison.Ordinal);
    Assert.DoesNotContain("elided", second.Content, StringComparison.Ordinal);
  }

  // ---- partial coverage: only held lines elide ----

  [Fact]
  public async Task PartialOverlap_ReturnsUncoveredLines_NamesElidedSpan()
  {
    ReadFreshnessLedger ledger = new();
    ReadTool tool = MakeTool(new FileRead(["a", "b", "c", "d"], 4, 4, V1), ledger);

    _ = await tool.ExecuteAsync(Args("""{"timeoutSeconds":120,"path":"f","startLine":2,"endLine":3}"""), TestContext.Current.CancellationToken);
    ToolResult widened = await tool.ExecuteAsync(Args("""{"timeoutSeconds":120,"path":"f","startLine":1,"endLine":4}"""), TestContext.Current.CancellationToken);

    Assert.False(widened.IsError);
    Assert.StartsWith("[read f lines 1-4 of 4 total | " + V1.Token + " | lines 2-3 elided (unchanged since your last read)]", widened.Content, StringComparison.Ordinal);
    Assert.Contains("1→ a", widened.Content, StringComparison.Ordinal);
    Assert.DoesNotContain("2→ b", widened.Content, StringComparison.Ordinal);
    Assert.DoesNotContain("3→ c", widened.Content, StringComparison.Ordinal);
    Assert.Contains("4→ d", widened.Content, StringComparison.Ordinal);
  }

  [Fact]
  public async Task AdjacentRanges_BothReturnFully_NoElision()
  {
    ReadFreshnessLedger ledger = new();
    ScriptedFileSystemAccess files = new(
        new FileRead(["a", "b"], 2, 4, V1),
        new FileRead(["c", "d"], 4, 4, V1));
    ReadTool tool = new(new StubPathResolver(), files, ledger);

    _ = await tool.ExecuteAsync(Args("""{"timeoutSeconds":120,"path":"f","startLine":1,"endLine":2}"""), TestContext.Current.CancellationToken);
    ToolResult next = await tool.ExecuteAsync(Args("""{"timeoutSeconds":120,"path":"f","startLine":3,"endLine":4}"""), TestContext.Current.CancellationToken);

    Assert.False(next.IsError);
    Assert.StartsWith("[read f lines 3-4 of 4 total | " + V1.Token + "]", next.Content, StringComparison.Ordinal);
    Assert.Contains("3→ c", next.Content, StringComparison.Ordinal);
    Assert.Contains("4→ d", next.Content, StringComparison.Ordinal);
  }

  // ---- clamp interplay ----

  [Fact]
  public async Task ClampedReRead_ElidesWithClampWarning()
  {
    ReadFreshnessLedger ledger = new();
    ReadTool tool = MakeTool(new FileRead(["a", "b", "c"], 3, 3, V1), ledger);

    _ = await tool.ExecuteAsync(Args("""{"timeoutSeconds":120,"path":"f","startLine":1,"endLine":3}"""), TestContext.Current.CancellationToken);
    ToolResult again = await tool.ExecuteAsync(Args("""{"timeoutSeconds":120,"path":"f","startLine":1,"endLine":100}"""), TestContext.Current.CancellationToken);

    Assert.False(again.IsError);
    Assert.StartsWith("[read f lines 1-3 of 3 total | " + V1.Token + " | unchanged since your last read; content elided]", again.Content, StringComparison.Ordinal);
    Assert.Contains("[warning] endLine 100 exceeded file length (3); clamped", again.Content, StringComparison.Ordinal);
  }

  // ---- legacy: version-less read is byte-identical ----

  [Fact]
  public async Task LegacyRead_NoVersion_NoToken_NoElision()
  {
    ReadFreshnessLedger ledger = new();
    ReadTool tool = MakeTool(new FileRead(["alpha", "beta"], 2, 2, null), ledger);

    ToolResult first = await tool.ExecuteAsync(Args("""{"timeoutSeconds":120,"path":"f","startLine":1,"endLine":2}"""), TestContext.Current.CancellationToken);
    ToolResult second = await tool.ExecuteAsync(Args("""{"timeoutSeconds":120,"path":"f","startLine":1,"endLine":2}"""), TestContext.Current.CancellationToken);

    Assert.False(first.IsError);
    Assert.StartsWith("[read f lines 1-2 of 2 total]", first.Content, StringComparison.Ordinal);
    Assert.False(second.IsError);
    Assert.StartsWith("[read f lines 1-2 of 2 total]", second.Content, StringComparison.Ordinal);
    Assert.Contains("1→ alpha", second.Content, StringComparison.Ordinal);
  }

  [Fact]
  public async Task NoLedger_VersionedRead_NoElisionButToken()
  {
    // A tool built without a ledger (legacy wiring) annotates the token but never elides.
    ReadTool tool = MakeTool(new FileRead(["alpha"], 1, 1, V1));

    ToolResult first = await tool.ExecuteAsync(Args("""{"timeoutSeconds":120,"path":"f","startLine":1,"endLine":1}"""), TestContext.Current.CancellationToken);
    ToolResult second = await tool.ExecuteAsync(Args("""{"timeoutSeconds":120,"path":"f","startLine":1,"endLine":1}"""), TestContext.Current.CancellationToken);

    Assert.StartsWith("[read f lines 1-1 of 1 total | " + V1.Token + "]", first.Content, StringComparison.Ordinal);
    Assert.StartsWith("[read f lines 1-1 of 1 total | " + V1.Token + "]", second.Content, StringComparison.Ordinal);
    Assert.Contains("1→ alpha", second.Content, StringComparison.Ordinal);
  }

  // ---- force parsing ----

  [Fact]
  public async Task ForceNotBoolean_ReturnsTypeError()
  {
    ReadTool tool = MakeTool(new FileRead(["alpha"], 1, 1, V1));
    ToolResult result = await tool.ExecuteAsync(Args("""{"timeoutSeconds":120,"path":"f","startLine":1,"endLine":1,"force":"yes"}"""), TestContext.Current.CancellationToken);
    Assert.True(result.IsError);
    Assert.Contains("force", result.Content, StringComparison.Ordinal);
    Assert.Contains("boolean", result.Content, StringComparison.Ordinal);
  }

  [Fact]
  public async Task UnknownParameterStillRejected_AfterForceAdded()
  {
    ReadTool tool = MakeTool(new FileRead(["alpha"], 1, 1, V1));
    ToolResult result = await tool.ExecuteAsync(Args("""{"timeoutSeconds":120,"path":"f","startLine":1,"endLine":1,"encoding":"utf16"}"""), TestContext.Current.CancellationToken);
    Assert.True(result.IsError);
    Assert.Contains("Unknown parameter", result.Content, StringComparison.Ordinal);
  }

  // ---- definition contract ----

  [Fact]
  public void Definition_ForceOptional_RequiredSubsetStated()
  {
    ReadTool tool = MakeTool(new FileRead(["alpha"], 1, 1, V1));
    Assert.Equal(5, tool.Definition.Parameters.Count);
    Assert.Equal(["timeoutSeconds", "path", "startLine", "endLine"], tool.Definition.RequiredParameters);
    Assert.Contains(tool.Definition.Parameters, p => p.Name == "force" && p.Type == ToolParameterType.Flag);
  }

  // ---- RootedAt preserves the ledger ----

  [Fact]
  public async Task RootedAt_PreservesLedger()
  {
    ReadFreshnessLedger ledger = new();
    ReadTool tool = MakeTool(new FileRead(["alpha"], 1, 1, V1), ledger);
    ITool rooted = tool.RootedAt("C:\\ws");

    ToolResult first = await rooted.ExecuteAsync(Args("""{"timeoutSeconds":120,"path":"f","startLine":1,"endLine":1}"""), TestContext.Current.CancellationToken);
    ToolResult second = await rooted.ExecuteAsync(Args("""{"timeoutSeconds":120,"path":"f","startLine":1,"endLine":1}"""), TestContext.Current.CancellationToken);

    Assert.False(first.IsError);
    // The re-rooted view shares the ledger: the second read elides.
    Assert.Contains("unchanged since your last read", second.Content, StringComparison.Ordinal);
  }

  // ---- fakes ----

  private sealed class StubPathResolver : IPathResolver
  {
    public Result<string> Resolve(string path) => Result.Success(path);
  }

  private sealed class FixedFileSystemAccess(FileRead read) : IFileSystemAccess
  {
    public Task<Result<FileRead>> ReadLinesAsync(string path, int startLine, int endLine, CancellationToken ct = default)
        => Task.FromResult(Result.Success(read));

    public Task<Result<byte[]>> ReadBytesAsync(string path, CancellationToken ct = default)
        => throw new NotImplementedException();
  }

  private sealed class ScriptedFileSystemAccess(params FileRead[] reads) : IFileSystemAccess
  {
    private readonly Queue<FileRead> _reads = new(reads);

    public Task<Result<FileRead>> ReadLinesAsync(string path, int startLine, int endLine, CancellationToken ct = default)
        => Task.FromResult(Result.Success(_reads.Count > 0 ? _reads.Dequeue() : reads[^1]));

    public Task<Result<byte[]>> ReadBytesAsync(string path, CancellationToken ct = default)
        => throw new NotImplementedException();
  }
}

#pragma warning restore JSON002
