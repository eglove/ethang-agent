using eThangAgent.SharedKernel;
using eThangAgent.ToolDomain.FileClaims;

namespace eThangAgent.ToolDomain.Tests;

/// <summary>Tool-layer file-claim checks (issue #364): edit/write consult the claim
/// gate before writing; a collision returns a typed Error [FileClaimHeld] naming
/// the holder session and expiry as a tool result; the holder renews on each
/// successful mutating call (issue #358 option (a), 15-minute TTL, no timer);
/// a null gate leaves results byte-identical. Shell() bypasses this seam and is
/// out of scope: claims are a cooperative guard, not a lock.</summary>
public class FileClaimToolTests
{
  private const string Root = @"C:\ws";
  private const string Resolved = @"C:\ws\a.cs";
  private const string EditArgs = "{\"timeoutSeconds\":120,\"path\":\"a.cs\",\"old\":\"x\",\"replacement\":\"y\",\"occurrences\":1}";
  private const string WriteArgs = "{\"timeoutSeconds\":120,\"path\":\"a.cs\",\"content\":\"x\",\"overwrite\":true}";

  private sealed class FakeEditAccess : IFileEditAccess
  {
    public int Calls { get; private set; }
    public Task<Result<ReplaceOutcome>> ReplaceInFileAsync(
        string path, string oldText, string newText, int? occurrences, CancellationToken ct = default)
    {
      Calls++;
      return Task.FromResult(Result.Success(new ReplaceOutcome(1, 3, LineEnding.Lf, [2])));
    }
    public Task<Result<ReplaceOutcome>> ReplaceLineRangeAsync(
        string path, int startLine, int endLine, string newText, CancellationToken ct = default)
    {
      Calls++;
      return Task.FromResult(Result.Success(new ReplaceOutcome(1, 3, LineEnding.Lf, [2])));
    }
    public Task<Result<HunkReplaceOutcome>> ReplaceHunksAsync(
        string path, IReadOnlyList<EditHunk> hunks, CancellationToken ct = default)
    {
      Calls++;
      return Task.FromResult(Result.Success(new HunkReplaceOutcome([1], 3, LineEnding.Lf, [2])));
    }
  }

  private sealed class FakeWriteAccess : IFileWriteAccess
  {
    public int Calls { get; private set; }
    public Task<Result<FileWriteOutcome>> WriteFileAsync(
        string path, string content, bool overwrite, CancellationToken ct = default)
    {
      Calls++;
      return Task.FromResult(Result.Success(new FileWriteOutcome(true, 42, LineEnding.Lf, [1, 2, 3])));
    }
    public Task<Result<FileWriteOutcome>> WriteFileLinesAsync(
        string path, IReadOnlyList<string> lines, bool overwrite, CancellationToken ct = default)
    {
      Calls++;
      return Task.FromResult(Result.Success(new FileWriteOutcome(true, 42, LineEnding.Lf, [1, 2, 3])));
    }
    public Task<Result<FileWriteOutcome>> WriteFileBytesAsync(
        string path, byte[] bytes, bool overwrite, CancellationToken ct = default)
    {
      Calls++;
      return Task.FromResult(Result.Success(new FileWriteOutcome(true, 42, LineEnding.Lf, [1, 2, 3])));
    }
  }

  private static EditTool EditToolWith(FakeEditAccess files, IFileClaimGate? gate)
    => new(new WorkspacePathResolver(Root), files, null, null, gate);

  private static WriteTool WriteToolWith(FakeWriteAccess files, IFileClaimGate? gate)
    => new(new WorkspacePathResolver(Root), files, null, null, gate);

  [Fact]
  public async Task TwoSessions_SameFile_SecondRefusedNamingHolderAndExpiry_FirstIntact()
  {
    SharedFileClaimStore store = new();
    DateTimeOffset now = DateTimeOffset.UtcNow;
    FakeEditAccess firstFiles = new();
    EditTool first = EditToolWith(firstFiles, new FileClaimGate(store, "ws-1", "s-1", () => now));
    ToolResult firstResult = await first.ExecuteAsync(new RawToolInput("edit", EditArgs), ct: TestContext.Current.CancellationToken);
    Assert.False(firstResult.IsError);
    Assert.Equal(1, firstFiles.Calls);

    FakeEditAccess secondFiles = new();
    EditTool second = EditToolWith(secondFiles, new FileClaimGate(store, "ws-1", "s-2", () => now));
    ToolResult secondResult = await second.ExecuteAsync(new RawToolInput("edit", EditArgs), ct: TestContext.Current.CancellationToken);
    Assert.True(secondResult.IsError);
    Assert.Contains("Error [FileClaimHeld]:", secondResult.Content, StringComparison.Ordinal);
    Assert.Contains("s-1", secondResult.Content, StringComparison.Ordinal);
    Assert.Equal(0, secondFiles.Calls);
    FileClaim? peek = store.Peek("ws-1", Resolved, now);
    Assert.NotNull(peek);
    Assert.Equal("s-1", peek.SessionId);
  }

  [Fact]
  public async Task SameSession_ReEdit_RenewsAndSucceeds()
  {
    SharedFileClaimStore store = new();
    DateTimeOffset t0 = DateTimeOffset.UtcNow;
    DateTimeOffset now = t0;
    FakeEditAccess files = new();
    EditTool tool = EditToolWith(files, new FileClaimGate(store, "ws-1", "s-1", () => now));
    ToolResult first = await tool.ExecuteAsync(new RawToolInput("edit", EditArgs), ct: TestContext.Current.CancellationToken);
    Assert.False(first.IsError);
    now = t0 + TimeSpan.FromMinutes(10);
    ToolResult second = await tool.ExecuteAsync(new RawToolInput("edit", EditArgs), ct: TestContext.Current.CancellationToken);
    Assert.False(second.IsError);
    Assert.Equal(2, files.Calls);
    FileClaim? peek = store.Peek("ws-1", Resolved, now);
    Assert.NotNull(peek);
    Assert.Equal(now + FileClaimPolicy.ClaimTtl, peek.Expiry);
  }

  [Fact]
  public async Task NullGate_ByteIdentical()
  {
    FakeEditAccess files = new();
    EditTool tool = EditToolWith(files, null);
    ToolResult result = await tool.ExecuteAsync(new RawToolInput("edit", EditArgs), ct: TestContext.Current.CancellationToken);
    Assert.False(result.IsError);
    Assert.Equal($"[edit {Resolved}] replaced 1 occurrence, file now 3 lines, LF line endings", result.Content);
  }

  [Fact]
  public async Task TwoSessions_Write_SecondRefused()
  {
    SharedFileClaimStore store = new();
    DateTimeOffset now = DateTimeOffset.UtcNow;
    FakeWriteAccess firstFiles = new();
    ToolResult first = await WriteToolWith(firstFiles, new FileClaimGate(store, "ws-1", "s-1", () => now))
        .ExecuteAsync(new RawToolInput("write", WriteArgs), ct: TestContext.Current.CancellationToken);
    Assert.False(first.IsError);
    FakeWriteAccess secondFiles = new();
    ToolResult second = await WriteToolWith(secondFiles, new FileClaimGate(store, "ws-1", "s-2", () => now))
        .ExecuteAsync(new RawToolInput("write", WriteArgs), ct: TestContext.Current.CancellationToken);
    Assert.True(second.IsError);
    Assert.Contains("Error [FileClaimHeld]:", second.Content, StringComparison.Ordinal);
    Assert.Equal(0, secondFiles.Calls);
  }

  [Fact]
  public async Task DifferentFiles_BothGranted()
  {
    SharedFileClaimStore store = new();
    DateTimeOffset now = DateTimeOffset.UtcNow;
    FakeEditAccess filesA = new();
    ToolResult a = await EditToolWith(filesA, new FileClaimGate(store, "ws-1", "s-1", () => now))
        .ExecuteAsync(new RawToolInput("edit", EditArgs), ct: TestContext.Current.CancellationToken);
    Assert.False(a.IsError);
    FakeEditAccess filesB = new();
    ToolResult b = await EditToolWith(filesB, new FileClaimGate(store, "ws-1", "s-2", () => now))
        .ExecuteAsync(new RawToolInput("edit", "{\"timeoutSeconds\":120,\"path\":\"b.cs\",\"old\":\"x\",\"replacement\":\"y\",\"occurrences\":1}"), ct: TestContext.Current.CancellationToken);
    Assert.False(b.IsError);
  }

  [Fact]
  public void RootedAt_CarriesGate()
  {
    SharedFileClaimStore store = new();
    EditTool root = EditToolWith(new FakeEditAccess(), new FileClaimGate(store, "ws-1", "s-1", () => DateTimeOffset.UtcNow));
    EditTool rooted = Assert.IsType<EditTool>(root.RootedAt(@"C:\ws\child"));
    Assert.Equal(root.Definition.Name, rooted.Definition.Name);
  }
}
