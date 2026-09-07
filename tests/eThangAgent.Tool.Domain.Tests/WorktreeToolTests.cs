using eThangAgent.SharedKernel;

namespace eThangAgent.ToolDomain.Tests;

/// <summary>Contract tests for the 'worktree' tool: strict input parsing, exact
///     output-contract lines, InvalidName gating before the seam, and verbatim
///     pass-through of resolver and seam failure codes.</summary>
public class WorktreeToolTests
{
  private const string Root = @"C:\ws";

  private static WorktreeTool Make(FakeGitWorktreeAccess fake) =>
      new(new WorkspacePathResolver(Root), fake);

  private static WorktreeInfo Wt(
      string name, string branch, bool isMain = false, bool isDirty = false) =>
      new(name, Path.Combine(Root, name), branch, "a1b2c3d", isMain, isDirty);

  private static string Args(string inner) =>
      "{" + "\"timeoutSeconds\":120" + (inner.Length == 0 ? "" : "," + inner) + "}";

  // ---- Input contract ----

  [Fact]
  public async Task MissingAction_ReturnsError()
  {
    WorktreeTool tool = Make(new FakeGitWorktreeAccess());
    ToolResult result = await tool.ExecuteAsync(new RawToolInput("worktree", Args("")),
        ct: TestContext.Current.CancellationToken);
    Assert.True(result.IsError);
    Assert.Contains("MissingParameter", result.Content, StringComparison.Ordinal);
    Assert.Contains("'action'", result.Content, StringComparison.Ordinal);
  }

  [Theory]
  [InlineData("bogus")]
  [InlineData("List")]
  [InlineData("CREATE")]
  public async Task UnknownAction_FailsWithInvalidAction(string action)
  {
    WorktreeTool tool = Make(new FakeGitWorktreeAccess());
    ToolResult result = await tool.ExecuteAsync(new RawToolInput("worktree",
        Args("\"action\":\"" + action + "\"")), ct: TestContext.Current.CancellationToken);
    Assert.True(result.IsError);
    Assert.Contains("InvalidAction", result.Content, StringComparison.Ordinal);
  }

  [Fact]
  public async Task WrongTypeAction_Rejected()
  {
    WorktreeTool tool = Make(new FakeGitWorktreeAccess());
    ToolResult result = await tool.ExecuteAsync(new RawToolInput("worktree",
        Args("\"action\":42")), ct: TestContext.Current.CancellationToken);
    Assert.True(result.IsError);
    Assert.Contains("InvalidParameterType", result.Content, StringComparison.Ordinal);
  }

  [Fact]
  public async Task UnknownParameter_Rejected()
  {
    WorktreeTool tool = Make(new FakeGitWorktreeAccess());
    ToolResult result = await tool.ExecuteAsync(new RawToolInput("worktree",
        Args("\"action\":\"list\",\"stat\":true")), ct: TestContext.Current.CancellationToken);
    Assert.True(result.IsError);
    Assert.Contains("Unknown parameter", result.Content, StringComparison.Ordinal);
    Assert.Contains("stat", result.Content, StringComparison.Ordinal);
  }

  [Theory]
  [InlineData("create")]
  [InlineData("remove")]
  public async Task MissingName_Rejected(string action)
  {
    WorktreeTool tool = Make(new FakeGitWorktreeAccess());
    ToolResult result = await tool.ExecuteAsync(new RawToolInput("worktree",
        Args("\"action\":\"" + action + "\"")), ct: TestContext.Current.CancellationToken);
    Assert.True(result.IsError);
    Assert.Contains("MissingParameter", result.Content, StringComparison.Ordinal);
    Assert.Contains("'name'", result.Content, StringComparison.Ordinal);
  }

  [Fact]
  public async Task NameOnList_IsRejectedAsUnknown()
  {
    WorktreeTool tool = Make(new FakeGitWorktreeAccess());
    ToolResult result = await tool.ExecuteAsync(new RawToolInput("worktree",
        Args("\"action\":\"list\",\"name\":\"x\"")), ct: TestContext.Current.CancellationToken);
    Assert.True(result.IsError);
    Assert.Contains("Unknown parameter", result.Content, StringComparison.Ordinal);
    Assert.Contains("name", result.Content, StringComparison.Ordinal);
  }

  [Fact]
  public async Task ForceOnCreate_IsRejectedAsUnknown()
  {
    WorktreeTool tool = Make(new FakeGitWorktreeAccess());
    ToolResult result = await tool.ExecuteAsync(new RawToolInput("worktree",
        Args("\"action\":\"create\",\"name\":\"x\",\"force\":true")),
        ct: TestContext.Current.CancellationToken);
    Assert.True(result.IsError);
    Assert.Contains("Unknown parameter", result.Content, StringComparison.Ordinal);
  }

  [Fact]
  public async Task WrongTypeName_Rejected()
  {
    WorktreeTool tool = Make(new FakeGitWorktreeAccess());
    ToolResult result = await tool.ExecuteAsync(new RawToolInput("worktree",
        Args("\"action\":\"create\",\"name\":7")), ct: TestContext.Current.CancellationToken);
    Assert.True(result.IsError);
    Assert.Contains("InvalidParameterType", result.Content, StringComparison.Ordinal);
  }

  [Fact]
  public async Task ForceNotBoolean_Rejected()
  {
    WorktreeTool tool = Make(new FakeGitWorktreeAccess());
    ToolResult result = await tool.ExecuteAsync(new RawToolInput("worktree",
        Args("\"action\":\"remove\",\"name\":\"x\",\"force\":\"yes\"")),
        ct: TestContext.Current.CancellationToken);
    Assert.True(result.IsError);
    Assert.Contains("InvalidParameterType", result.Content, StringComparison.Ordinal);
  }

  // ---- InvalidName gate: validation happens before the seam ----

  [Theory]
  [InlineData("")]
  [InlineData(" ")]
  [InlineData("Bad Name")]
  [InlineData("Caps")]
  public async Task InvalidName_NeverTouchesTheSeam(string name)
  {
    FakeGitWorktreeAccess fake = new();
    WorktreeTool tool = Make(fake);
    string encoded = System.Text.Json.JsonEncodedText.Encode(name).ToString();
    ToolResult result = await tool.ExecuteAsync(new RawToolInput("worktree",
        Args("\"action\":\"create\",\"name\":\"" + encoded + "\"")),
        ct: TestContext.Current.CancellationToken);
    Assert.True(result.IsError);
    Assert.Contains("InvalidName", result.Content, StringComparison.Ordinal);
    Assert.Equal(0, fake.CreateCallCount);
  }

  // ---- list: output contract ----

  [Fact]
  public async Task List_RendersExactHeaderAndEntryLines()
  {
    FakeGitWorktreeAccess fake = new();
    fake.Seed(
        new WorktreeInfo("main", Root, "refs/heads/main", "a1b2c3d", IsMain: true, IsDirty: true),
        new WorktreeInfo("agent-fix", Path.Combine(Root, "agent-fix"), "refs/heads/agent-fix", "a1b2c3d", IsMain: false, IsDirty: false));
    WorktreeTool tool = Make(fake);
    ToolResult result = await tool.ExecuteAsync(new RawToolInput("worktree",
        Args("\"action\":\"list\"")), ct: TestContext.Current.CancellationToken);
    Assert.False(result.IsError);
    string[] lines = result.Content.Split('\n');
    Assert.Equal(3, lines.Length);
    Assert.Equal("[worktree] name | path | branch | sha | flags", lines[0]);
    Assert.Equal("main | . | refs/heads/main | a1b2c3d | [main] [dirty]", lines[1]);
    Assert.Equal("agent-fix | agent-fix | refs/heads/agent-fix | a1b2c3d | ", lines[2]);
  }

  [Fact]
  public async Task List_PassesResolvedRootToTheSeam()
  {
    FakeGitWorktreeAccess fake = new();
    WorktreeTool tool = Make(fake);
    ToolResult result = await tool.ExecuteAsync(new RawToolInput("worktree",
        Args("\"action\":\"list\"")), ct: TestContext.Current.CancellationToken);
    Assert.False(result.IsError);
    string line = Assert.Single(fake.ListCalls);
    Assert.Equal(Root, line);
  }

  [Fact]
  public async Task List_WithNoWorktrees_RendersHeaderOnly()
  {
    FakeGitWorktreeAccess fake = new();
    WorktreeTool tool = Make(fake);
    ToolResult result = await tool.ExecuteAsync(new RawToolInput("worktree",
        Args("\"action\":\"list\"")), ct: TestContext.Current.CancellationToken);
    Assert.False(result.IsError);
    Assert.Equal("[worktree] name | path | branch | sha | flags", result.Content);
  }

  // ---- create ----

  [Fact]
  public async Task Create_HappyPath_CallsSeamWithResolvedRootAndName()
  {
    FakeGitWorktreeAccess fake = new();
    WorktreeTool tool = Make(fake);
    ToolResult result = await tool.ExecuteAsync(new RawToolInput("worktree",
        Args("\"action\":\"create\",\"name\":\"agent-fix\"")),
        ct: TestContext.Current.CancellationToken);
    Assert.False(result.IsError);
    Assert.Equal(1, fake.CreateCallCount);
    Assert.Equal("agent-fix", fake.CreateCalls[0]);
    Assert.Equal("[worktree] created agent-fix at C:\\ws\\agent-fix", result.Content);
  }

  [Fact]
  public async Task Create_Duplicate_SurfacesWorktreeExistsVerbatim()
  {
    FakeGitWorktreeAccess fake = new()
    {
      FailOnCreate = new DomainError("WorktreeExists", "a worktree named 'agent-fix' already exists")
    };
    WorktreeTool tool = Make(fake);
    ToolResult result = await tool.ExecuteAsync(new RawToolInput("worktree",
        Args("\"action\":\"create\",\"name\":\"agent-fix\"")),
        ct: TestContext.Current.CancellationToken);
    Assert.True(result.IsError);
    Assert.Contains("Error [WorktreeExists]: a worktree named 'agent-fix' already exists",
        result.Content, StringComparison.Ordinal);
  }

  // ---- remove ----

  [Fact]
  public async Task Remove_DirtyWithoutForce_SurfacesWorktreeDirty()
  {
    FakeGitWorktreeAccess fake = new()
    {
      FailOnRemove = new DomainError("WorktreeDirty", "worktree 'agent-fix' has uncommitted changes; pass force to discard")
    };
    WorktreeTool tool = Make(fake);
    ToolResult result = await tool.ExecuteAsync(new RawToolInput("worktree",
        Args("\"action\":\"remove\",\"name\":\"agent-fix\"")),
        ct: TestContext.Current.CancellationToken);
    Assert.True(result.IsError);
    Assert.Contains("Error [WorktreeDirty]:", result.Content, StringComparison.Ordinal);
  }

  [Fact]
  public async Task Remove_DirtyWithForce_SucceedsAndForwardsForce()
  {
    FakeGitWorktreeAccess fake = new();
    fake.Seed(Wt("agent-fix", "refs/heads/agent-fix", isDirty: true));
    WorktreeTool tool = Make(fake);
    ToolResult result = await tool.ExecuteAsync(new RawToolInput("worktree",
        Args("\"action\":\"remove\",\"name\":\"agent-fix\",\"force\":true")),
        ct: TestContext.Current.CancellationToken);
    Assert.False(result.IsError);
    Assert.Equal("[worktree] removed agent-fix", result.Content);
    (string RepoRoot, string Name, bool Force) call = Assert.Single(fake.RemoveCalls);
    Assert.Equal((Root, "agent-fix", true), call);
  }

  [Fact]
  public async Task Remove_Main_SurfacesMainWorktreeVerbatim()
  {
    FakeGitWorktreeAccess fake = new()
    {
      FailOnRemove = new DomainError("MainWorktree", "the main worktree cannot be removed")
    };
    WorktreeTool tool = Make(fake);
    ToolResult result = await tool.ExecuteAsync(new RawToolInput("worktree",
        Args("\"action\":\"remove\",\"name\":\"main\"")),
        ct: TestContext.Current.CancellationToken);
    Assert.True(result.IsError);
    Assert.Contains("Error [MainWorktree]:", result.Content, StringComparison.Ordinal);
  }

  // ---- resolver and seam failures pass through ----

  [Fact]
  public async Task ResolverFailure_SurfacesResolverCodeVerbatim()
  {
    FakeGitWorktreeAccess fake = new();
    FailingResolver failingResolver = new(new DomainError("WorkspaceMissing", "no such workspace"));
    WorktreeTool tool = new(failingResolver, fake);
    ToolResult result = await tool.ExecuteAsync(new RawToolInput("worktree",
        Args("\"action\":\"list\"")), ct: TestContext.Current.CancellationToken);
    Assert.True(result.IsError);
    Assert.Contains("Error [WorkspaceMissing]: no such workspace", result.Content, StringComparison.Ordinal);
    Assert.Empty(fake.ListCalls);
  }

  [Fact]
  public async Task List_SeamFailure_SurfacesCodeVerbatim()
  {
    FakeGitWorktreeAccess fake = new()
    {
      FailOnList = new DomainError("NotAGitRepository", "not a git repository: " + Root)
    };
    WorktreeTool tool = Make(fake);
    ToolResult result = await tool.ExecuteAsync(new RawToolInput("worktree",
        Args("\"action\":\"list\"")), ct: TestContext.Current.CancellationToken);
    Assert.True(result.IsError);
    Assert.Contains("Error [NotAGitRepository]: not a git repository: " + Root,
        result.Content, StringComparison.Ordinal);
  }

  /// <summary>Resolver stub whose Resolve always fails with one fixed error.</summary>
  private sealed class FailingResolver(DomainError error) : IPathResolver
  {
    public Result<string> Resolve(string path) => Result.Failure<string>(error);
  }
}
