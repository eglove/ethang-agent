using eThangAgent.ToolDomain.Verification;

namespace eThangAgent.ToolDomain.Tests.Verification;

public class VerificationCommandSpecificationTests
{
  private static VerificationCommandSpecification Default() =>
      new(["dotnet test", "dotnet build", "dotnet format", "npm test", "pytest", "cargo test", "go test"]);

  private static ShellExecutionRecord Run(params string[] tokens) =>
      new(tokens, 0, DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow);

  [Fact]
  public void DotnetTest_ExitZero_Matches() =>
      Assert.True(Default().IsSatisfiedBy(Run("dotnet", "test")));

  [Fact]
  public void DotnetTest_ExitOne_DoesNotMatch()
  {
    ShellExecutionRecord r = new(["dotnet", "test"], 1,
        DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow);
    Assert.False(Default().IsSatisfiedBy(r));
  }

  [Fact]
  public void DotnetRestore_DoesNotMatch() =>
      Assert.False(Default().IsSatisfiedBy(Run("dotnet", "restore")));

  [Fact]
  public void DotnetBuild_Matches() =>
      Assert.True(Default().IsSatisfiedBy(Run("dotnet", "build")));

  [Fact]
  public void FullPathDotnet_MatchesBarePrefix() =>
      Assert.True(Default().IsSatisfiedBy(Run(@"C:\Program Files\dotnet\dotnet.exe", "test")));

  [Fact]
  public void ExeOnly_IsCaseInsensitive_ArgumentCaseIsSignificant() =>
      Assert.False(Default().IsSatisfiedBy(Run("dotnet", "TEST")));

  [Fact]
  public void NpmTest_Matches() =>
      Assert.True(Default().IsSatisfiedBy(Run("npm", "test")));

  [Fact]
  public void ShorterRecord_NeverMatchesLongerPrefix() =>
      Assert.False(Default().IsSatisfiedBy(Run("dotnet")));

  [Fact]
  public void EmptyPrefixList_IsRejected() =>
      _ = Assert.Throws<ArgumentOutOfRangeException>(() => new VerificationCommandSpecification([]));

  [Fact]
  public void EmptyTokenPrefix_IsRejected() =>
      _ = Assert.Throws<ArgumentOutOfRangeException>(
          () => new VerificationCommandSpecification(["   "]));
}
