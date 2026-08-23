using eThangAgent.SharedKernel;
using eThangAgent.Terminal.ACL;
using eThangAgent.ToolDomain;

namespace eThangAgent.CLI;

/// <summary>
///     Clarify channel for the interactive terminal: writes the question compactly
///     on the input row (the only row the frame loop never overwrites), runs a minimal
///     key loop — printables append, Backspace erases, Enter submits. Ctrl+C or end of
///     keys cancels. The question is a single line showing the prompt and every option;
///     the human answers on the same row inline.
/// </summary>
public sealed class InteractiveClarifyChannel(ITextWriter writer, IKeyReader reader) : IClarifyChannel
{
    public Task<Result<string>> AskAsync(ClarifyQuestion question, CancellationToken ct = default)
    {
        // Build a compact single-line prompt: "Q? [1) a, 2) b]" or "Q?"
        var prompt = question.Question;
        if (question.Options.Count > 0)
        {
            var opts = string.Join(", ",
                question.Options.Select((o, i) => $"{i + 1}) {o}"));
            prompt += $" [{opts}]";
        }
        if (question.AllowFreeText)
            prompt += " (or type)";

        // Write on the current row (the input row the frame loop never overwrites)
        // and pad with spaces so stale text from the editor prompt is covered.
        var pad = Math.Max(0, writer.BufferWidth - prompt.Length - 2);
        writer.Write(prompt + ": " + new string(' ', pad));
        writer.SetCursorPosition(prompt.Length + 2, writer.CursorTop);

        var buffer = new List<char>();
        while (true)
        {
            var key = reader.ReadKey();
            if (key is null)
                return Cancelled("Input ended before an answer was given.");

            if (key.Value.Key == ConsoleKey.C && key.Value.Modifiers.HasFlag(ConsoleModifiers.Control))
                return Cancelled("Cancelled by the user (Ctrl+C).");

            switch (key.Value.Key)
            {
                case ConsoleKey.Enter:
                    writer.WriteLine(string.Empty);
                    return Task.FromResult(Result<string>.Success(new string(buffer.ToArray())));

                case ConsoleKey.Backspace:
                    if (buffer.Count > 0)
                    {
                        buffer.RemoveAt(buffer.Count - 1);
                        writer.Write(" ");
                    }
                    break;

                default:
                    var c = key.Value.KeyChar;
                    if (!char.IsControl(c))
                    {
                        buffer.Add(c);
                        writer.Write(c.ToString());
                    }
                    break;
            }
        }
    }

    private static Task<Result<string>> Cancelled(string message) =>
        Task.FromResult(Result<string>.Failure(new Error("Cancelled", message)));
}
