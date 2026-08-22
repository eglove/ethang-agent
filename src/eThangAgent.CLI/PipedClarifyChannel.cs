using eThangAgent.SharedKernel;
using eThangAgent.ToolDomain;

namespace eThangAgent.CLI;

/// <summary>
///     Clarify channel for redirected input (pipes, E2E runs): reads one line from the
///     reader as the raw answer. End of input cancels.
/// </summary>
public sealed class PipedClarifyChannel(TextReader reader) : IClarifyChannel
{
    public Task<Result<string>> AskAsync(ClarifyQuestion question, CancellationToken ct = default)
    {
        var line = reader.ReadLine();
        return Task.FromResult(line is not null
            ? Result<string>.Success(line)
            : Result<string>.Failure(new Error("Cancelled",
                "Input ended before an answer was given.")));
    }
}
