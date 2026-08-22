using System.Diagnostics;
using eThangAgent.SharedKernel;

namespace eThangAgent.MemoryDomain.Tests;

public class BoundedRegexTests
{
    private static string Rendered(Result<IReadOnlyList<int>> result)
        => $"Error [{result.Error!.Code}]: {result.Error.Message}";

    [Fact]
    public void Limits_ArePortedVerbatimFromPiFabric()
    {
        Assert.Equal(1024, BoundedRegex.MaxPatternBytes);
        Assert.Equal(2 * 1024 * 1024, BoundedRegex.MaxHaystackBytes);
        Assert.Equal(250, BoundedRegex.TimeoutMs);
    }

    [Fact]
    public void Execute_NullHaystacks_ReturnsOkEmpty()
    {
        var result = BoundedRegex.Execute("needle", null!);

        Assert.True(result.IsSuccess);
        Assert.Empty(result.Value!);
    }

    [Fact]
    public void Execute_EmptyHaystacks_ReturnsOkEmpty()
    {
        var result = BoundedRegex.Execute("needle", []);

        Assert.True(result.IsSuccess);
        Assert.Empty(result.Value!);
    }

    [Fact]
    public void Execute_PatternExceedingMaxPatternBytes_FailsWithExactTooLargeError()
    {
        var result = BoundedRegex.Execute(new string('a', 1100), ["x"]);

        Assert.False(result.IsSuccess);
        Assert.Equal(
            "Error [regex_pattern_too_large]: Regex pattern exceeds 1024 bytes.",
            Rendered(result));
    }

    [Fact]
    public void Execute_MalformedPattern_FailsStartingWithInvalidRegex()
    {
        var result = BoundedRegex.Execute("([", ["x"]);

        Assert.False(result.IsSuccess);
        Assert.StartsWith("Error [invalid_regex]:", Rendered(result));
    }

    [Fact]
    public void Execute_CatastrophicBacktracking_FailsWithExactTimeoutError_WellUnderFiveSeconds()
    {
        var watch = Stopwatch.StartNew();

        var result = BoundedRegex.Execute("(a+)+$", [new string('a', 5000) + "b"]);

        watch.Stop();
        Assert.False(result.IsSuccess);
        Assert.Equal(
            "Error [regex_timeout]: Regex exceeded the 250 ms budget.",
            Rendered(result));
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5), $"took {watch.Elapsed}");
    }

    [Fact]
    public void Execute_IsCaseInsensitive_ReturnsMatchingIndicesInOrder()
    {
        var result = BoundedRegex.Execute("hello", ["say HELLO", "nope", "hello world"]);

        Assert.True(result.IsSuccess);
        Assert.Equal(new[] { 0, 2 }, result.Value);
    }

    [Fact]
    public void Execute_HaystackBeyondByteCap_TruncatesSoTailNeedleIsNotSearched()
    {
        const int totalChars = 3 * 1024 * 1024;
        ReadOnlySpan<char> tail = "needle";
        var haystack = new string('x', totalChars - tail.Length) + tail.ToString();

        var result = BoundedRegex.Execute("needle", [haystack]);

        Assert.True(result.IsSuccess);
        Assert.Empty(result.Value!);
    }

    [Fact]
    public void Execute_NeedleAtEndOfCapSizedHaystack_IsFound()
    {
        var haystack =
            new string('x', BoundedRegex.MaxHaystackBytes - "needle".Length) + "needle";

        var result = BoundedRegex.Execute("needle", [haystack]);

        Assert.True(result.IsSuccess);
        Assert.Equal(new[] { 0 }, result.Value);
    }
}
