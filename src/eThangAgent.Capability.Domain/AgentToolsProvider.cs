using eThangAgent.ToolDomain;

namespace eThangAgent.CapabilityDomain;

/// <summary>Exposes existing ITool instances as capability actions. Read's behavior,
///     format contract, and tests are unchanged — this is a pure adapter.
///     Every action is <see cref="TimeoutPolicy.SelfManaged"/>: ITool contracts parse
///     their own timeoutSeconds envelope (ToolCallEnvelopeParser) and bound themselves
///     via ToolExecution.</summary>
public sealed class AgentToolsProvider : ICapabilityProvider
{
  private readonly Dictionary<string, ITool> _tools;
  private readonly IReadOnlyList<AgentToolBinding> _bindings;

  public AgentToolsProvider(string id, IReadOnlyList<AgentToolBinding> bindings)
  {
    Id = id ?? throw new ArgumentNullException(nameof(id));
    ArgumentNullException.ThrowIfNull(bindings);
    _bindings = bindings;
    _tools = bindings.ToDictionary(b => b.Tool.Definition.Name, b => b.Tool, StringComparer.Ordinal);
    Actions = [.. bindings.Select(b => new ActionDescriptor(
        b.Tool.Definition.Name,
        b.Summary,
        b.Tool.Definition.Description,
        [.. b.Tool.Definition.Parameters.Select(p => new ActionParameter(p.Name, p.Type.ToString(), p.Description))],
        b.Tool.Definition.RequiredParameters,
        TimeoutPolicy.SelfManaged))];
  }


  public string Id { get; }

  public IReadOnlyList<ActionDescriptor> Actions { get; }

  /// <summary>The anchored view of this provider over <paramref name="anchor"/>: a NEW
  ///     provider whose IWorkspaceScopedTool bindings resolve paths at the anchor
  ///     (RootedAt — same definition, resolver swapped) and whose unscoped bindings pass
  ///     through unchanged. Anchoring changes path resolution, never advertisement —
  ///     the same doctrine AnchoredToolRegistry applies to the loop registry.</summary>
  public AgentToolsProvider RootedAt(string anchor)
  {
    return string.IsNullOrWhiteSpace(anchor)
        ? throw new ArgumentException("Anchor must be a non-empty workspace root.", nameof(anchor))
        : new AgentToolsProvider(Id,
            [.. _bindings.Select(b => b.Tool is IWorkspaceScopedTool scoped
                ? new AgentToolBinding(scoped.RootedAt(anchor), b.Summary)
                : b)]);
  }

  public async Task<CapabilityInvocationResult> InvokeAsync(
      string actionName, string jsonArguments, CancellationToken ct = default)
  {
    if (!_tools.TryGetValue(actionName, out ITool? tool))
    {
      return CapabilityInvocationResult.Fail($"Error [UnknownAction]: Unknown action: {actionName}.");
    }

    ToolResult result = await tool.ExecuteAsync(new RawToolInput(actionName, jsonArguments), ct).ConfigureAwait(false);
    return new CapabilityInvocationResult(result.Content, result.IsError);
  }
}
