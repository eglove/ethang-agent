using eThangAgent.ModelDomain;

namespace eThangAgent.Composition;

/// <summary>Per-container holder for the workspace's smart-selection options. The
/// factory fills it after its preference read; the resolvers read it lazily per
/// selection. Mutable by design: construction-phase glue, like SessionFilesCarrier.
/// </summary>
public sealed class SmartSelectionCarrier
{
  public SmartSelectionOptions? Current { get; private set; }

  public void Fill(SmartSelectionOptions? options) => Current = options;
}
