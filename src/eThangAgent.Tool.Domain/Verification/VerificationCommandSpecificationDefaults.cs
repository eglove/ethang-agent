namespace eThangAgent.ToolDomain.Verification;

/// <summary>The default verification command set (spec #24). A workspace
///     preference may replace it wholesale; the list itself never changes here.</summary>
public static class VerificationCommandSpecificationDefaults
{
  public static readonly string[] Commands =
  [
    "dotnet test",
    "dotnet build",
    "dotnet format",
    "npm test",
    "pytest",
    "cargo test",
    "go test",
  ];
}
