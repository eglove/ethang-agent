namespace eThangAgent.Mcp.ACL.Tests;

/// <summary>The sampling gate (issue #109): a server asking the harness's model for a
///     completion is billed to the user without the user in the loop. The harness
///     NEVER advertises the sampling capability (nor elicitation - the SDK deprecates
///     both as of specification 2026-07-28, SEP-2577) - a sampling request fails at
///     the SDK's capability check, structurally, before any handler could exist.
///     Per-server enablement is deliberately absent: the refusal is the safe default
///     and there is no consumer demand for the inverse. The options builder is the
///     pinning point; the assertions ride reflection because the SDK flags the
///     capability members obsolete (MCP9005) - the deprecation ITSELF is the
///     structural refusal, and the test pins that the factory sets none of them.</summary>
public class SdkClientCapabilityTests
{
  [Fact]
  public void BuildClientOptions_Never_Advertises_Sampling_Or_Elicitation()
  {
    ModelContextProtocol.Client.McpClientOptions options = SdkMcpClientSessionFactory.BuildClientOptions();

    Assert.NotNull(options.Capabilities);
    System.Reflection.PropertyInfo? sampling = options.Capabilities.GetType().GetProperty("Sampling");
    System.Reflection.PropertyInfo? elicitation = options.Capabilities.GetType().GetProperty("Elicitation");
    Assert.NotNull(sampling);
    Assert.Null(sampling.GetValue(options.Capabilities));
    Assert.NotNull(elicitation);
    Assert.Null(elicitation.GetValue(options.Capabilities));
  }
}
