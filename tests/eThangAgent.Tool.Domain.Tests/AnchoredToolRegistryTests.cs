namespace eThangAgent.ToolDomain.Tests;

/// <summary>AnchoredToolRegistry: re-roots every IWorkspaceScopedTool found in the
///     inner registry at the anchor, passing unscoped tools through untouched. The
///     child-run anchoring mechanism (task 7) resolves on this.</summary>
public class AnchoredToolRegistryTests
{
  private const string Anchor = @"C:\anchor\ws";

  // ── scoped resolution ───────────────────────────────────────────────────

  [Fact]
  public void Find_ScopedTool_ResolvesRootedAtAnchor()
  {
    ScopedFakeTool scoped = new("scoped", "rooted");
    AnchoredToolRegistry registry = new(new StubRegistry(scoped), Anchor);

    ITool? found = registry.Find("scoped");

    Assert.Equal(Anchor, scoped.RootedAtArg);
    Assert.NotNull(found);
    Assert.Same(scoped.RootedSentinel, found);
  }

  [Fact]
  public void Find_UnknownName_ResolvesExactlyAsInner()
  {
    AnchoredToolRegistry registry = new(new StubRegistry(), Anchor);

    ITool? found = registry.Find("nope");

    Assert.Null(found);
  }

  // ── pass-through ────────────────────────────────────────────────────────

  [Fact]
  public void Find_UnscopedTool_PassesThroughUnchanged()
  {
    FakeTool plain = new(new ToolDefinition("plain", "desc", []));
    AnchoredToolRegistry registry = new(new StubRegistry(plain), Anchor);

    ITool? found = registry.Find("plain");

    Assert.Same(plain, found);
  }

  [Fact]
  public void Definitions_DelegatesToInner()
  {
    FakeTool plain = new(new ToolDefinition("plain", "desc", []));
    ScopedFakeTool scoped = new("scoped", "rooted");
    StubRegistry inner = new(plain, scoped);
    AnchoredToolRegistry registry = new(inner, Anchor);

    IReadOnlyList<ToolDefinition> defs = registry.Definitions;

    Assert.Equal(inner.Definitions, defs);
    Assert.Equal(2, defs.Count);
  }

  // ── construction guards ─────────────────────────────────────────────────

  [Fact]
  public void Constructor_NullInner_ThrowsArgumentNullException() =>
      _ = Assert.Throws<ArgumentNullException>(() => new AnchoredToolRegistry(null!, Anchor));

  [Fact]
  public void Constructor_NullAnchor_ThrowsArgumentNullException() =>
      _ = Assert.Throws<ArgumentNullException>(() => new AnchoredToolRegistry(new StubRegistry(), null!));

  [Theory]
  [InlineData("")]
  [InlineData("   ")]
  public void Constructor_WhitespaceAnchor_ThrowsArgumentException(string anchor) =>
      _ = Assert.Throws<ArgumentException>(() => new AnchoredToolRegistry(new StubRegistry(), anchor));

  [Fact]
  public void Constructor_InnerIsAnchoredToolRegistry_Throws()
  {
    AnchoredToolRegistry inner = new(new StubRegistry(), Anchor);

    _ = Assert.Throws<ArgumentException>(() => new AnchoredToolRegistry(inner, Anchor));
  }

  // ── fakes ───────────────────────────────────────────────────────────────

  private sealed class FakeTool(ToolDefinition def) : ITool
  {
    public ToolDefinition Definition { get; } = def;

    public Task<ToolResult> ExecuteAsync(RawToolInput input, CancellationToken ct = default)
        => throw new NotImplementedException();
  }

  /// <summary>Records the root it was asked to root at; RootedAt returns a distinct
  ///     sentinel instance so the test can pin that resolution produced a new tool.</summary>
  private sealed class ScopedFakeTool(string name, string sentinelName) : ITool, IWorkspaceScopedTool
  {
    public ToolDefinition Definition { get; } = new(name, "desc", []);

    public string? RootedAtArg { get; private set; }

    public ITool RootedSentinel { get; } = new FakeTool(new ToolDefinition(sentinelName, "desc", []));

    public ITool RootedAt(string workspaceRoot)
    {
      RootedAtArg = workspaceRoot;
      return RootedSentinel;
    }

    public Task<ToolResult> ExecuteAsync(RawToolInput input, CancellationToken ct = default)
        => throw new NotImplementedException();
  }

  private sealed class StubRegistry(params ITool[] tools) : IToolRegistry
  {
    private readonly Dictionary<string, ITool> _tools = tools.ToDictionary(t => t.Definition.Name, StringComparer.Ordinal);

    public bool DefinitionsQueried { get; private set; }

    public ITool? Find(string name) => _tools.TryGetValue(name, out ITool? tool) ? tool : null;

    public IReadOnlyList<ToolDefinition> Definitions
    {
      get
      {
        DefinitionsQueried = true;
        return [.. _tools.Values.Select(t => t.Definition)];
      }
    }
  }
}
