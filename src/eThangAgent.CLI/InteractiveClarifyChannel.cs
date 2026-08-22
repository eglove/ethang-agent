using eThangAgent.SharedKernel;
using eThangAgent.Terminal.ACL;
using eThangAgent.ToolDomain;

namespace eThangAgent.CLI;

/// <summary>
///     Clarify channel for the interactive terminal: renders the question and its
///     numbered options on the writer, then runs a minimal key loop — printables append,
///     Backspace erases, Enter submits. Ctrl+C or end of keys cancels.
/// </summary>
public sealed class InteractiveClarifyChannel(ITextWriter writer, IKeyReader reader) : IClarifyChannel
{
    public Task<Result<string>> AskAsync(ClarifyQuestion question, CancellationToken ct = default)
    {
        writer.WriteLine(question.Question);
        for (var i = 0; i < question.Options.Count; i++)
            writer.WriteLine($"{i + 1}) {question.Options[i]}");
        if (question.AllowFreeText)
            writer.WriteLine("(type your own answer, or a number to choose)");

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
                        writer.Write("\b \b");
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
