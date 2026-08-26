namespace eThangAgent.Desktop.Streaming;

internal abstract record UiStreamEvent
{
  internal sealed record Delta(string Text) : UiStreamEvent;
  internal sealed record Reasoning(string Text) : UiStreamEvent;
  internal sealed record IterationEnd() : UiStreamEvent;
  internal sealed record ToolCallEvent(string Name, string Arguments) : UiStreamEvent;
  internal sealed record ToolResultEvent(string Name, string Summary) : UiStreamEvent;
}
