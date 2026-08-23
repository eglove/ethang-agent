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
        // The full question and its options are already visible in the transcript
        // pane (the clarify tool-call entry). The input row gets a short, always-fits
        // answer prompt — never a column past the buffer width, which would throw
        // ArgumentOutOfRangeException from SetCursorPosition.
        var prompt = question.Options.Count > 0
            ? $"answer [1-{question.Options.Count}]"
            : "answer";
        if (question.AllowFreeText)
            prompt += " or type";
        prompt += ": ";

        writer.Write(prompt);
        var cursorCol = Math.Min(prompt.Length, writer.BufferWidth - 1);
        writer.SetCursorPosition(cursorCol, writer.CursorTop);

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
