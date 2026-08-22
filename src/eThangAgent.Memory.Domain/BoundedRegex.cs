using System.Text;
using System.Text.RegularExpressions;
using eThangAgent.SharedKernel;

namespace eThangAgent.MemoryDomain;

/// <summary>
/// Executes untrusted regex patterns against a corpus of haystack strings under
/// hard budgets ported verbatim from pi-fabric <c>search.ts</c>: pattern size,
/// per-haystack content size, and a wall-clock match timeout.
/// </summary>
/// <remarks>
/// Budget deviation-for-simplicity: pi-fabric enforces the byte budget across
/// the whole batch inside a disposable worker; ours applies the cap per entry —
/// batch accounting stays a caller concern (the seam is noted for the search
/// service). Truncation is char-based (see the loop below), so multi-byte
/// content can marginally exceed the byte cap; that upper-bound slack is part
/// of the same documented simplification.
/// </remarks>
public static class BoundedRegex
{
    public const int MaxPatternBytes = 1024;
    public const int MaxHaystackBytes = 2 * 1024 * 1024;
    public const int TimeoutMs = 250;

    /// <summary>
    /// Returns the indices of every haystack matched by <paramref name="pattern"/>,
    /// or a typed failure: oversized pattern, unparseable pattern, or timeout.
    /// A timeout ends the run immediately — later haystacks are not tested.
    /// </summary>
    public static Result<IReadOnlyList<int>> Execute(string pattern, IReadOnlyList<string> haystacks)
    {
        if (haystacks is null || haystacks.Count == 0)
            return Result<IReadOnlyList<int>>.Success([]);

        if (Encoding.UTF8.GetByteCount(pattern) > MaxPatternBytes)
            return Result<IReadOnlyList<int>>.Failure(
                new Error("regex_pattern_too_large", $"Regex pattern exceeds {MaxPatternBytes} bytes."));

        Regex regex;
        try
        {
            regex = new Regex(
                pattern,
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
                TimeSpan.FromMilliseconds(TimeoutMs));
        }
        catch (ArgumentException ex)
        {
            return Result<IReadOnlyList<int>>.Failure(new Error("invalid_regex", ex.Message));
        }

        List<int> matches = [];
        for (var index = 0; index < haystacks.Count; index++)
        {
            var content = haystacks[index];

            // Per-entry budget: content past the cap is truncated to its first
            // MaxHaystackBytes characters before testing. Char-based truncation is a
            // deliberate simplification (see type remarks); the tail beyond the cap
            // is never searched.
            if (Encoding.UTF8.GetByteCount(content) > MaxHaystackBytes)
                content = content[..MaxHaystackBytes];

            try
            {
                if (regex.IsMatch(content))
                    matches.Add(index);
            }
            catch (RegexMatchTimeoutException)
            {
                return Result<IReadOnlyList<int>>.Failure(
                    new Error("regex_timeout", $"Regex exceeded the {TimeoutMs} ms budget."));
            }
        }

        return Result<IReadOnlyList<int>>.Success(matches);
    }
}
