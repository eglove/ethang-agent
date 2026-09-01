using System.Text.Json;

namespace eThangAgent.AgentDomain.Tests;

/// <summary>Validator subset: type/properties/required checks; malformed report JSON is a
///     validation failure with feedback, malformed schema is an infrastructure fault.</summary>
public class ResultSchemaValidatorTests
{
  [Fact]
  public void ValidObject_Passes()
  {
    SchemaValidation result = ResultSchemaValidator.Validate(
        """{"type":"object","required":["summary"],"properties":{"summary":{"type":"string"}}}""",
        """{"summary":"all done","detail":"x"}""");

    Assert.True(result.IsValid);
    Assert.Null(result.Error);
    Assert.NotNull(result.NormalizedJson);
  }

  [Fact]
  public void MissingRequired_Fails_WithFeedback()
  {
    SchemaValidation result = ResultSchemaValidator.Validate(
        """{"type":"object","required":["summary"]}""", """{}""");

    Assert.False(result.IsValid);
    Assert.Contains("summary", result.Error, StringComparison.Ordinal);
  }

  [Fact]
  public void WrongPropertyType_Fails()
  {
    SchemaValidation result = ResultSchemaValidator.Validate(
        """{"type":"object","properties":{"count":{"type":"number"}}}""",
        """{"count":"many"}""");

    Assert.False(result.IsValid);
    Assert.Contains("count", result.Error, StringComparison.Ordinal);
  }

  [Fact]
  public void WrongTopLevelType_Fails()
  {
    SchemaValidation result = ResultSchemaValidator.Validate("""{"type":"array"}""", """{}""");
    Assert.False(result.IsValid);
  }

  [Fact]
  public void MalformedReport_IsValidationFailure_NotException()
  {
    SchemaValidation result = ResultSchemaValidator.Validate("""{"type":"object"}""", "not json");
    Assert.False(result.IsValid);
    Assert.Contains("not valid JSON", result.Error, StringComparison.Ordinal);
  }

  [Fact]
  public void MalformedSchema_IsInfrastructureFault()
      => _ = Assert.ThrowsAny<JsonException>(() => ResultSchemaValidator.Validate("not json", "{}"));
}
