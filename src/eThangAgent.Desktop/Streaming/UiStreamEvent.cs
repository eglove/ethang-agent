namespace eThangAgent.Desktop.Streaming;

internal abstract record UiStreamEvent
{
  internal sealed record Delta(string Text) : UiStreamEvent;
  internal sealed record Reasoning(string Text) : UiStreamEvent;
  internal sealed record IterationEnd() : UiStreamEvent;
  internal sealed record ToolCallEvent(string Name, string Arguments) : UiStreamEvent;
  internal sealed record ToolResultEvent(string Name, string Summary, string FullContent, bool IsError) : UiStreamEvent;

  /// <summary>A turn notice (model selection, fallback announcements). Rides the
  ///     bridge like every other turn-voice event because the pipeline raises notices
  ///     on the turn thread — applying them inline would mutate the UI-owned
  ///     transcript collection cross-thread.</summary>
  internal sealed record Notice(string Text) : UiStreamEvent;
}
