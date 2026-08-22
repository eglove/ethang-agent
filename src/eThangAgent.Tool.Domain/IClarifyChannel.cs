using eThangAgent.SharedKernel;

namespace eThangAgent.ToolDomain;

/// <summary>A question the clarify tool wants answered by the human.</summary>
public sealed record ClarifyQuestion(string Question, IReadOnlyList<string> Options, bool AllowFreeText);

/// <summary>Seam between the clarify tool and whatever can reach the human.</summary>
public interface IClarifyChannel
{
    Task<Result<string>> AskAsync(ClarifyQuestion question, CancellationToken ct = default);
}
