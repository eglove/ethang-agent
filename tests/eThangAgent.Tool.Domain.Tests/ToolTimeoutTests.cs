using System.Text.Json;
using eThangAgent.SharedKernel;

namespace eThangAgent.ToolDomain.Tests;

/// <summary>The required per-call execution budget: every tool argument object must carry
///     timeoutSeconds, strictly validated. Missing/wrong type/non-positive/over-cap are
///     typed errors; nothing is coerced or defaulted.</summary>
public class ToolTimeoutTests
{
  private static Result<TimeSpan> Parse(string json)
  {
    Result<JsonElement> parsed = ToolArguments.ParseObject(json);
    return !parsed.IsSuccess ? Result.Failure<TimeSpan>(parsed.Error!) : ToolTimeout.Parse(parsed.Value);
  }

  [Fact]
  public void Valid_Budget_Parses()
  {
    Result<TimeSpan> r = Parse(/*lang=json,strict*/ """{"timeoutSeconds": 30}""");
    Assert.True(r.IsSuccess);
    Assert.Equal(TimeSpan.FromSeconds(30), r.Value);
  }

  [Fact]
  public void Max_Budget_Is_Accepted()
  {
    Result<TimeSpan> r = Parse($$"""{"timeoutSeconds": {{ToolTimeout.MaxSeconds}}}""");
    Assert.True(r.IsSuccess);
  }

  [Fact]
  public void Missing_Timeout_Fails_MissingParameter()
  {
    Result<TimeSpan> r = Parse("{}");
    Assert.False(r.IsSuccess);
    Assert.Equal("MissingParameter", r.Error!.Code);
    Assert.Contains("timeoutSeconds", r.Error.Message, StringComparison.Ordinal);
  }

  [Theory]
  [InlineData(/*lang=json,strict*/ """{"timeoutSeconds": "30"}""", "InvalidParameterType")]
  [InlineData(/*lang=json,strict*/ """{"timeoutSeconds": true}""", "InvalidParameterType")]
  [InlineData(/*lang=json,strict*/ """{"timeoutSeconds": 0}""", "InvalidParameterValue")]
  [InlineData(/*lang=json,strict*/ """{"timeoutSeconds": -5}""", "InvalidParameterValue")]
  [InlineData(/*lang=json,strict*/ """{"timeoutSeconds": 3601}""", "InvalidParameterValue")]
  public void Invalid_Timeouts_Fail_WithTypedErrors(string json, string expectedCode)
  {
    Result<TimeSpan> r = Parse(json);
    Assert.False(r.IsSuccess);
    Assert.Equal(expectedCode, r.Error!.Code);
  }

  [Fact]
  public void Malformed_Json_Fails()
  {
    Result<TimeSpan> r = Parse("{bad");
    Assert.False(r.IsSuccess);
    Assert.Equal("InvalidJsonArguments", r.Error!.Code);
  }

  [Fact]
  public void Non_Object_Arguments_Fail()
  {
    Result<TimeSpan> r = Parse("[1]");
    Assert.False(r.IsSuccess);
    Assert.Equal("InvalidJsonArguments", r.Error!.Code);
  }

  [Fact]
  public void TimedOut_Result_Documents_The_Contract()
  {
    ToolResult result = ToolTimeout.TimedOut("read", TimeSpan.FromSeconds(45));
    Assert.True(result.IsError);
    Assert.StartsWith("Error [ToolTimeout]:", result.Content, StringComparison.Ordinal);
    Assert.Contains("'read'", result.Content, StringComparison.Ordinal);
    Assert.Contains("45s", result.Content, StringComparison.Ordinal);
    Assert.Contains("timeoutSeconds", result.Content, StringComparison.Ordinal);
  }
}
