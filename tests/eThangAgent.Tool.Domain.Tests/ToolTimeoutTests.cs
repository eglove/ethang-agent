using eThangAgent.SharedKernel;
using eThangAgent.ToolDomain;

namespace eThangAgent.ToolDomain.Tests;

/// <summary>The required per-call execution budget: every tool argument object must carry
///     timeoutSeconds, strictly validated. Missing/wrong type/non-positive/over-cap are
///     typed errors; nothing is coerced or defaulted.</summary>
public class ToolTimeoutTests
{
    private static Result<TimeSpan> Parse(string json)
    {
        var parsed = ToolArguments.ParseObject(json);
        if (!parsed.IsSuccess)
            return Result<TimeSpan>.Failure(parsed.Error!);
        return ToolTimeout.Parse(parsed.Value);
    }

    [Fact]
    public void Valid_Budget_Parses()
    {
        var r = Parse("""{"timeoutSeconds": 30}""");
        Assert.True(r.IsSuccess);
        Assert.Equal(TimeSpan.FromSeconds(30), r.Value);
    }

    [Fact]
    public void Max_Budget_Is_Accepted()
    {
        var r = Parse($$"""{"timeoutSeconds": {{ToolTimeout.MaxSeconds}}}""");
        Assert.True(r.IsSuccess);
    }

    [Fact]
    public void Missing_Timeout_Fails_MissingParameter()
    {
        var r = Parse("{}");
        Assert.False(r.IsSuccess);
        Assert.Equal("MissingParameter", r.Error!.Code);
        Assert.Contains("timeoutSeconds", r.Error.Message);
    }

    [Theory]
    [InlineData("""{"timeoutSeconds": "30"}""", "InvalidParameterType")]
    [InlineData("""{"timeoutSeconds": true}""", "InvalidParameterType")]
    [InlineData("""{"timeoutSeconds": 0}""", "InvalidParameterValue")]
    [InlineData("""{"timeoutSeconds": -5}""", "InvalidParameterValue")]
    [InlineData("""{"timeoutSeconds": 3601}""", "InvalidParameterValue")]
    public void Invalid_Timeouts_Fail_WithTypedErrors(string json, string expectedCode)
    {
        var r = Parse(json);
        Assert.False(r.IsSuccess);
        Assert.Equal(expectedCode, r.Error!.Code);
    }

    [Fact]
    public void Malformed_Json_Fails()
    {
        var r = Parse("{bad");
        Assert.False(r.IsSuccess);
        Assert.Equal("InvalidJsonArguments", r.Error!.Code);
    }

    [Fact]
    public void Non_Object_Arguments_Fail()
    {
        var r = Parse("[1]");
        Assert.False(r.IsSuccess);
        Assert.Equal("InvalidJsonArguments", r.Error!.Code);
    }

    [Fact]
    public void TimedOut_Result_Documents_The_Contract()
    {
        var result = ToolTimeout.TimedOut("read", TimeSpan.FromSeconds(45));
        Assert.True(result.IsError);
        Assert.StartsWith("Error [ToolTimeout]:", result.Content);
        Assert.Contains("'read'", result.Content);
        Assert.Contains("45s", result.Content);
        Assert.Contains("timeoutSeconds", result.Content);
    }
}
