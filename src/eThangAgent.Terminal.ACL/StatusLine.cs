namespace eThangAgent.Terminal.ACL;

/// <summary>Single-row statusline: model, message count, and current state, padded to the full width.</summary>
public sealed class StatusLine
{
    public void Render(ITextWriter writer, int row, int width, string model, int messageCount, string state)
    {
        var text = $" {model} │ {messageCount} msgs │ {state}";
        if (text.Length > width)
            text = text[..width];

        writer.SetCursorPosition(0, row);
        writer.Write(text.PadRight(width)[..width]);
    }
}
