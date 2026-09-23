namespace eThangAgent.ToolDomain;

/// <summary>Capability question over the resolved session model: does the model
///     accept image input? The vision flag on ModelConfig carries the catalog's
///     answer; an implementation of this interface exposes it to Tool Domain
///     consumers (e.g. a screenshot tool deciding attach-vs-withhold) without the
///     Tool Domain reaching into the Model Domain. The adapter lives in the
///     composition root, over the session's resolved ModelConfig.</summary>
public interface IImageInputCapability
{
  /// <summary>True when the session's resolved model accepts image input.</summary>
  bool AcceptsImages { get; }
}
