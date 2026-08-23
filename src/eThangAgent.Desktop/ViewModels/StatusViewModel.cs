namespace eThangAgent.Desktop.ViewModels;

/// <summary>Turn phase for status display. Task 13 replaces this stub with an animated indicator.</summary>
public enum TurnPhase
{
    Ready,
    Thinking,
    Streaming,
}

/// <summary>Minimal status stub — exposes the model id and the current turn phase.
/// Task 13 extends this with the animated indicator.</summary>
public sealed class StatusViewModel(string modelId)
{
    public string ModelId { get; } = modelId;
    public TurnPhase Phase { get; set; } = TurnPhase.Ready;
}
