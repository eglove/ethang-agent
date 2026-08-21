namespace eThangAgent.Terminal.ACL;

/// <summary>Terminal text output with the cursor operations line editing needs.</summary>
public interface ITextWriter
{
    int CursorLeft { get; }
    int CursorTop { get; }
    int BufferWidth { get; }

    void SetCursorPosition(int left, int top);
    void Write(string value);
    void Write(string value, ConsoleColor foreground);
    void WriteLine(string value);
}
