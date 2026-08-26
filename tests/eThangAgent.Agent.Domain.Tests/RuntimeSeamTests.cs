namespace eThangAgent.AgentDomain.Tests;

public class RuntimeSeamTests
{
  // The runtime/runner seams are exercised through the InProcessAgentRuntime and
  // SubAgentSpawner suites; these tests pin only the canonical error strings.

  [Fact]
  public void Error_Constants_AreAnnotated_AndDistinct()
  {
    Guid id = Guid.NewGuid();
    string[] errors =
    [
            RuntimeErrors.CapReached,
            RuntimeErrors.NotFound(id),
            RuntimeErrors.NotComplete(id),
        ];
    Assert.All(errors, e => Assert.StartsWith("Error [", e, StringComparison.Ordinal));
    Assert.Equal(3, errors.Distinct().Count());
    Assert.Contains(id.ToString(), RuntimeErrors.NotFound(id), StringComparison.Ordinal);
    Assert.Contains(id.ToString(), RuntimeErrors.NotComplete(id), StringComparison.Ordinal);
  }
}
