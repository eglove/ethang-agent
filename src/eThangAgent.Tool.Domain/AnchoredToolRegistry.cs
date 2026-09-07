namespace eThangAgent.ToolDomain;

/// <summary>A registry view in which every <see cref="IWorkspaceScopedTool"/> the inner
///     registry resolves is re-rooted at the anchor (a child run's workspace root):
///     resolution serves <c>RootedAt(anchor)</c> for scoped tools and passes every other
///     tool through unchanged; unknown names resolve exactly as inner resolves them.
///     Wrapping is single — construction refuses an inner <see cref="AnchoredToolRegistry"/>:
///     the no-double-wrap guard is this class's own, enforced here (FilteredToolRegistry has
///     no such guard; its callers stay single by their own discipline). Definitions are
///     delegated to inner untouched: anchoring changes path resolution, never advertisement.</summary>
public sealed class AnchoredToolRegistry : IToolRegistry
{
  private readonly IToolRegistry _inner;
  private readonly string _anchor;

  /// <summary>Wraps <paramref name="inner"/>, re-rooting every workspace-scoped tool it
  ///     resolves at <paramref name="anchor"/>.</summary>
  public AnchoredToolRegistry(IToolRegistry inner, string anchor)
  {
    _inner = inner ?? throw new ArgumentNullException(nameof(inner));
    Inner = _inner;
    _anchor = anchor ?? throw new ArgumentNullException(nameof(anchor));

    if (string.IsNullOrWhiteSpace(anchor))
    {
      throw new ArgumentException("Anchor must be a non-empty workspace root.", nameof(anchor));
    }

    if (inner is AnchoredToolRegistry)
    {
      throw new ArgumentException("Registry is already anchored; double anchoring is not allowed.", nameof(inner));
    }
  }

  /// <summary>The wrapped registry this one delegates advertisement to.</summary>
  public IToolRegistry Inner { get; }

  public ITool? Find(string name)
  {
    ArgumentNullException.ThrowIfNull(name);
    ITool? tool = _inner.Find(name);
    return tool is IWorkspaceScopedTool scoped ? scoped.RootedAt(_anchor) : tool;
  }

  public IReadOnlyList<ToolDefinition> Definitions => _inner.Definitions;
}
