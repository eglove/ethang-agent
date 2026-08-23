namespace eThangAgent.Desktop.Streaming;

public abstract record UiStreamEvent
{
    public sealed record Delta(string Text) : UiStreamEvent;
    public sealed record Reasoning(string Text) : UiStreamEvent;
    public sealed record IterationEnd() : UiStreamEvent;
    public sealed record ToolCallEvent(string Name, string Arguments) : UiStreamEvent;
    public sealed record ToolResultEvent(string Name, string Summary) : UiStreamEvent;
}
