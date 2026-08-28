namespace eThangAgent.ToolDomain;

/// <summary>Canonical DomainError codes shared by tool-input validators.</summary>
public static class ToolErrorCodes
{
  /// <summary>A provided parameter carried the wrong JSON type.</summary>
  public const string InvalidParameterType = "InvalidParameterType";

  /// <summary>A provided parameter carried an unacceptable value.</summary>
  public const string InvalidParameterValue = "InvalidParameterValue";

  /// <summary>A required parameter was absent.</summary>
  public const string MissingParameter = "MissingParameter";

  /// <summary>An argument object carried a key the tool does not declare.</summary>
  public const string UnknownParameter = "UnknownParameter";
}
