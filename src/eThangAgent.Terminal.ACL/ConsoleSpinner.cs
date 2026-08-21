namespace eThangAgent.Terminal.ACL;

/// <summary>Animates a spinner on the current line while a task runs, then clears the line.</summary>
public sealed class ConsoleSpinner(ITextWriter writer, int intervalMs = 80, string[]? frames = null)
{
    private static readonly string[] DefaultFrames = ["\u280b", "\u2819", "\u2839", "\u2838", "\u283c", "\u2834", "\u2826", "\u2827", "\u2807", "\u280f"];

    public async Task RunWhile(Task task, string label)
    {
        if (task.IsCompleted)
            return;

        var sequence = frames ?? DefaultFrames;
        var i = 0;
        var lastLength = 0;

        while (!task.IsCompleted)
        {
            var frame = $"\r{sequence[i % sequence.Length]}  {label}\u2026";
            writer.Write(frame);
            lastLength = frame.Length;
            i++;
            await Task.Delay(intervalMs).ConfigureAwait(false);
        }

        writer.Write("\r" + new string(' ', Math.Max(lastLength, 1)) + "\r");
    }
}
