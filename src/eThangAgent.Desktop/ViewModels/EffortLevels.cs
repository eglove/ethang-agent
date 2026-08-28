using eThangAgent.ModelDomain;

namespace eThangAgent.Desktop.ViewModels;

/// <summary>The effort picker's shared vocabulary: the display name of every domain
///     reasoning-effort level (plus the model-default "choice"), used by both the
///     picker rows and the transcript notice announcing the change.</summary>
internal static class EffortLevels
{
  /// <summary>Human-readable name of a level; "Model default" for null.</summary>
  public static string DisplayName(ReasoningEffort? effort) => effort switch
  {
    null => "Model default",
    ReasoningEffort.Max => "Max",
    ReasoningEffort.ExtraHigh => "Extra High",
    ReasoningEffort.High => "High",
    ReasoningEffort.Medium => "Medium",
    ReasoningEffort.Low => "Low",
    ReasoningEffort.Minimal => "Minimal",
    ReasoningEffort.None => "None",
    _ => throw new ArgumentOutOfRangeException(nameof(effort), effort, "Unknown reasoning effort level."),
  };
}
